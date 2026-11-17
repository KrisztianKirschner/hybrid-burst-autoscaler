"""Preset calibration measurement (docs/csharp-workload-v1.2.md §10.2).

Closed loop: one job in flight at a time, so the queue never holds more than one job and the
autoscaler stays at one replica. Each job's worker-side time (finished_at - started_at: download +
process + upload) is appended to a CSV; analysis/calibration.ipynb evaluates it.

usage: python calibrate.py <preset> <rounds> <csv> [image ...]
  images default to img-01 .. img-07, the whole of source set v2
  HBA_API overrides the API base URL (default http://api.hba.internal:8080)

example: python loadtest/calibrate.py medium 5 analysis/data/calibration/presets-v1.csv

Two versions: the CSV name is the preset version (settings in Hba.Processing/ImagePresets.cs);
SOURCE_SET below is the image set version, written into every row. A new image set keeps
appending to the same presets-vN.csv; the notebook keeps the sets apart.
"""
import csv
import json
import os
import sys
import time
import urllib.request
from datetime import datetime

API = os.environ.get("HBA_API", "http://api.hba.internal:8080")
SOURCE_SET = "v2"   # must match the files in the bucket; loadtest/source-set/credits-v2.csv has their sha256
DEFAULT_IMAGES = [f"img-{i:02d}" for i in range(1, 8)]
FIELDS = ["source_set", "preset", "round", "image", "seconds", "job_id", "worker", "tier",
          "started_at", "finished_at"]


def call(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.load(r)


def run_job(key, preset):
    job_id = call("POST", "/jobs", {"input_key": key, "preset": preset})["job_id"]
    deadline = time.time() + 120
    while time.time() < deadline:
        job = call("GET", f"/jobs/{job_id}")
        if job["status"] in ("done", "failed"):
            return job
        time.sleep(0.2)
    raise TimeoutError(f"job {job_id} not finished after 120 s")


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    preset, rounds, path = sys.argv[1], int(sys.argv[2]), sys.argv[3]
    images = sys.argv[4:] or DEFAULT_IMAGES

    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    new_file = not os.path.exists(path)
    with open(path, "a", newline="") as f:
        out = csv.DictWriter(f, FIELDS)
        if new_file:
            out.writeheader()
        for rnd in range(1, rounds + 1):
            for img in images:
                job = run_job(f"src/{img}.jpg", preset)
                if job["status"] != "done":
                    sys.exit(f"{img}: job {job['job_id']} failed: {job.get('error')}")
                seconds = (datetime.fromisoformat(job["finished_at"])
                           - datetime.fromisoformat(job["started_at"])).total_seconds()
                out.writerow({"source_set": SOURCE_SET, "preset": preset, "round": rnd,
                              "image": img, "seconds": f"{seconds:.3f}", "job_id": job["job_id"],
                              "worker": job.get("worker"), "tier": job.get("tier"),
                              "started_at": job["started_at"], "finished_at": job["finished_at"]})
                f.flush()
            print(f"{preset}: round {rnd}/{rounds} done", flush=True)
    print(f"{rounds * len(images)} {preset} jobs appended to {path}")


if __name__ == "__main__":
    main()
