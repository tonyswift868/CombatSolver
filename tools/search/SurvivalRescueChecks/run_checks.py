"""Recreate the fixed combat roots and compare Release builds through the native offline harness."""
import argparse
import ctypes
import json
import os
from pathlib import Path
import subprocess
import time

REPO = Path(__file__).resolve().parents[3]
CORPUS = "coverage/corpora/novelty/"
BOSS = CORPUS + "dev-09-necrobinder-boss.json"
BOSS_FORCE = ["SHACKLING_POTION", "CURE_ALL"]
FORCE = {"potionSlotCount": 2, "potions": {"ids": ["POWER_POTION", "COLORLESS_POTION"]}}
BREW = {"potionSlotCount": 3, "potions": {"ids": [*FORCE["potions"]["ids"], "GAMBLERS_BREW"]}}
REGRESSIONS = [
    ("02-dev-09-necrobinder-boss", CORPUS + "dev-09-necrobinder-boss.json", {}, "victory"),
    ("14-dev-09-necrobinder-boss-hp56", CORPUS + "dev-09-necrobinder-boss.json", {"playerCurrentHp": 56}, "victory"),
    ("15-holdout-04-necrobinder-hp25", CORPUS + "holdout-04-necrobinder.json", FORCE | {"playerCurrentHp": 25}, "forced-victory"),
    ("15-holdout-04-necrobinder-hp28", CORPUS + "holdout-04-necrobinder.json", FORCE | {"playerCurrentHp": 28}, "rescue"),
    ("lossless-defeat", None, {"characterId": "IRONCLAD", "encounterId": "FUZZY_WURM_CRAWLER_WEAK",
        "enemyCurrentHp": 30, "initialPlayerHp": 1, "initialPlayerEnergy": 0,
        "clearPlayerHand": True, "clearPlayerPiles": True}, "defeat"),
]
BOSS_FORCE_CASES = [
    (f"{case[0]}-force-{mode}", BOSS, case[2], "rescue")
    for case in REGRESSIONS[:2] for mode in ("both", "0", "1")
]
REGRESSIONS += BOSS_FORCE_CASES
CONTROLS = [
    ("00-sentinel-potions", CORPUS + "sentinel-potions.json", {}),
    ("01-GA-IRONCLAD-ELITE-00", "coverage/corpora/strategy/roots/GA-IRONCLAD-ELITE-00-spec.json", {}),
    ("02-dev-01-silent-elite", CORPUS + "dev-01-silent-elite.json", {}),
    ("02-dev-06-silent-boss", CORPUS + "dev-06-silent-boss.json", {}),
    ("02-holdout-04-necrobinder", CORPUS + "holdout-04-necrobinder.json", {}),
    ("11-GA-SILENT-BOSS-00", "coverage/corpora/strategy/roots/GA-SILENT-BOSS-00-spec.json", BREW | {"playerCurrentHp": 7}),
    ("12-generated-09", "tools/search/GeneratedCombatScenarios/specified.json",
        BREW | {"seed": "V769-G09", "playerCurrentHp": 6, "encounterKind": "Monster"}),
    ("15-dev-04-necrobinder-elite-hp11", CORPUS + "dev-04-necrobinder-elite.json", FORCE | {"playerCurrentHp": 11}),
]

class MemoryStatus(ctypes.Structure):
    _fields_ = [("size", ctypes.c_ulong), ("load", ctypes.c_ulong)] + [
        (name, ctypes.c_uint64) for name in
        ("total_phys", "avail_phys", "total_commit", "avail_commit", "total_virtual", "avail_virtual", "extended")
    ]

def memory_available():
    status = MemoryStatus()
    status.size = ctypes.sizeof(status)
    while True:
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            raise ctypes.WinError()
        if min(status.avail_phys, status.avail_commit) >= 2 * 1024**3:
            return {"physicalMiB": status.avail_phys // 2**20, "commitMiB": status.avail_commit // 2**20}
        time.sleep(20)

def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))

def write(path, payload):
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

def run_case(args, case, side, dll):
    case_id, source, overrides, expectation = case
    dest = args.output / case_id / side
    dest.mkdir(parents=True)
    request = {"scenarioId": case_id, "schemaVersion": 1, "cards": []}
    if source:
        spec = dest / "spec.json"
        write(spec, read(REPO / source) | overrides)
        request["generatedScenarioPath"] = str(spec)
    else:
        request.update(overrides)
    request_path = dest / "request.json"
    write(request_path, request)
    settings_path = args.settings
    expected_force = None
    if case in BOSS_FORCE_CASES:
        mode = case_id.rsplit("-", 1)[1]
        settings = read(settings_path)
        settings["potionDirectives"] = [
            {"slot": slot, "potionId": potion, "directive": "Force"}
            for slot, potion in enumerate(BOSS_FORCE) if mode in ("both", str(slot))
        ]
        settings_path = dest / "settings.json"
        write(settings_path, settings)
        expected_force = {f"{item['slot']}:{item['potionId']}" for item in settings["potionDirectives"]}
    elif expectation == "rescue":
        expected_force = {"0:POWER_POTION", "1:COLORLESS_POTION"}
    memory = memory_available()
    env = os.environ | {"OFFLINE_HARNESS_COMBATSOLVER_DLL": str(dll)}
    with (dest / "run.log").open("wb") as output:
        check = subprocess.run(
            [str(Path(env["DOTNET_ROOT"]) / "dotnet.exe"), str(args.harness),
             str(request_path), str(settings_path), str(dest), expectation],
            env=env, cwd=REPO, stdout=output, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW, timeout=180)
    result_path = dest / "result.json"
    if not result_path.exists():
        raise RuntimeError(f"No result from {case_id}/{side}. Read {dest / 'run.log'}")
    row = read(result_path) | {"case": case_id, "side": side, "exit": check.returncode, "memory": memory}
    if expected_force is not None and set(row["forced"]) != expected_force:
        print(f"{case_id} activated {row['forced']}, expected {sorted(expected_force)}", flush=True)
        row["exit"] = 1
    print(case_id, side, f"exit={check.returncode} win={row['won']} onlyDeath={row['OnlyDeathRoutesFound']}",
          f"boundary={row['Boundary']} HP={row['PlayerHp']} native={row['nativeOutcome']}:{row['nativeHp']}",
          f"firstMs={row['FirstWinMs']}", flush=True)
    return row

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("harness", "settings", "output", "candidate", "baseline"):
        parser.add_argument(f"--{option}", type=lambda path: Path(path).resolve(), required=option != "baseline")
    parser.add_argument("--case", action="append")
    args = parser.parse_args()
    rows = []
    snapshots = ("Root", "RouteSha", "TotalExpandedNodes", "TotalTransitionCount", "PlayerHp")
    discrepancies = []
    for case in REGRESSIONS:
        if not args.case or case[0] in args.case:
            rows.append(run_case(args, case, "candidate", args.candidate))
    if args.baseline:
        for index, control in enumerate(CONTROLS):
            if args.case and control[0] not in args.case:
                continue
            arms = [("baseline", args.baseline), ("candidate", args.candidate)]
            if index % 2:
                arms.reverse()
            left, right = [run_case(args, (*control, "control"), side, dll) for side, dll in arms]
            rows.extend((left, right))
            different = [key for key in snapshots if left[key] != right[key]]
            if different:
                discrepancies.append({"case": control[0], "different": different})
    if not rows:
        parser.error("No case selected")
    write(args.output / "summary.json", {"runs": rows, "discrepancies": discrepancies})
    return int(bool(discrepancies) or any(row["exit"] for row in rows))

if __name__ == "__main__":
    raise SystemExit(main())
