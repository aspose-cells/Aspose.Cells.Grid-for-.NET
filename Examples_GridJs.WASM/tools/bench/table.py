"""Summarise tools/bench/out/*.json into the tables used by BENCHMARK.md.

python tools/bench/table.py [--out tools/bench/out]
"""
import argparse
import glob
import json
import os


def num(v):
    return "" if v is None else v


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
    a = ap.parse_args()

    rows = []
    for path in sorted(glob.glob(os.path.join(a.out, "*.json"))):
        d = json.load(open(path, encoding="utf-8"))
        pg = d.get("page") or {}
        bs, dr, m = pg.get("bootStats") or {}, pg.get("dataReq") or {}, pg.get("marks") or {}
        ch, sa, sb = d.get("memChromeAfterLoad") or {}, d.get("serverAfterLoad") or {}, d.get("serverBase") or {}
        rows.append({
            "label": d.get("label"), "engine": d.get("engine"), "ready": d.get("wallReadyMs"),
            "engineUp": bs.get("pageStart"), "jsonReady": bs.get("afterFirstJson"),
            "gridBuilt": m.get("loadDataReturned"), "render": m.get("sheetLoaded"),
            "transport": dr.get("ms"), "chars": dr.get("chars"), "parse": m.get("loadDataMs"),
            "heap": pg.get("heapMB"), "chromeWS": ch.get("wsMB"), "chromeCPU": ch.get("cpuSec"),
            "serverWS": sa.get("wsMB"),
            "serverCPU": None if sa.get("cpuSec") is None or sb.get("cpuSec") is None
            else round(sa["cpuSec"] - sb["cpuSec"], 2),
            "cell": (d.get("cell") or {}).get("totalMs"), "shape": (d.get("shape") or {}).get("totalMs"),
            "rows": pg.get("rows"), "cols": pg.get("cols"), "sheets": pg.get("sheets"),
            "shapes": pg.get("shapes"), "errs": len(pg.get("errs") or []),
        })

    cols = ["label", "engine", "ready", "engineUp", "jsonReady", "gridBuilt", "render", "transport",
            "chars", "parse", "heap", "chromeWS", "chromeCPU", "serverWS", "serverCPU", "cell",
            "shape", "rows", "cols", "sheets", "shapes", "errs"]
    print("| " + " | ".join(cols) + " |")
    print("|" + "---|" * len(cols))
    for r in sorted(rows, key=lambda x: (x["engine"] or "", x["label"] or "")):
        print("| " + " | ".join(str(num(r[c])) for c in cols) + " |")
    print("\n%d runs from %s" % (len(rows), a.out))


if __name__ == "__main__":
    main()
