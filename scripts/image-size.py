#!/usr/bin/env python3
"""Exact image sizes from an OCI layout tarball (docker buildx --output type=oci).

  scripts/image-size.py <oci.tar> <variant> <platform> [--max-compressed-mb N] [--max-uncompressed-mb N]

compressed   = sum of the layer blobs as stored/pulled from a registry (what `docker pull` downloads)
uncompressed = sum of the gunzipped layer tars (what lands on disk, roughly `docker image inspect .Size`)
Prints one JSON line; appends a markdown row to $GITHUB_STEP_SUMMARY when set. Exits 1 over budget.
"""
import argparse, json, os, sys, tarfile, zlib

ap = argparse.ArgumentParser()
ap.add_argument("tar"); ap.add_argument("variant"); ap.add_argument("platform")
ap.add_argument("--max-compressed-mb", type=float); ap.add_argument("--max-uncompressed-mb", type=float)
a = ap.parse_args()

t = tarfile.open(a.tar)
members = {m.name.removeprefix("./"): m for m in t.getmembers()}
def member(name): return t.extractfile(members[name])
def blob(d): return member("blobs/" + d.replace(":", "/"))
def js(d): return json.load(blob(d))

def manifests(desc):
    if desc["mediaType"].endswith(("image.index.v1+json", "manifest.list.v2+json")):
        for m in js(desc["digest"])["manifests"]:
            if m.get("annotations", {}).get("vnd.docker.reference.type") == "attestation-manifest":
                continue
            yield from manifests(m)
    else:
        yield desc

index = json.load(member("index.json"))
mans = [m for d in index["manifests"] for m in manifests(d)]
if len(mans) != 1:
    sys.exit(f"expected one image manifest, found {len(mans)}")
man = js(mans[0]["digest"])
comp = sum(l["size"] for l in man["layers"])
unc = 0
for l in man["layers"]:
    f, d = blob(l["digest"]), zlib.decompressobj(16 + zlib.MAX_WBITS) if "gzip" in l["mediaType"] else None
    while chunk := f.read(1 << 20):
        unc += len(d.decompress(chunk)) if d else len(chunk)
    if d: unc += len(d.flush())

mb = lambda b: round(b / 1024 / 1024, 1)
row = {"variant": a.variant, "platform": a.platform, "layers": len(man["layers"]),
       "compressedBytes": comp, "uncompressedBytes": unc, "compressedMiB": mb(comp), "uncompressedMiB": mb(unc)}
print(json.dumps(row))
if s := os.environ.get("GITHUB_STEP_SUMMARY"):
    with open(s, "a") as f:
        f.write(f"| {a.variant} | {a.platform} | {mb(comp)} MiB ({comp:,} B) | {mb(unc)} MiB ({unc:,} B) | {len(man['layers'])} |\n")
over = []
if a.max_compressed_mb and mb(comp) > a.max_compressed_mb: over.append(f"compressed {mb(comp)} > {a.max_compressed_mb} MiB")
if a.max_uncompressed_mb and mb(unc) > a.max_uncompressed_mb: over.append(f"uncompressed {mb(unc)} > {a.max_uncompressed_mb} MiB")
if over:
    print(f"::error::{a.variant} {a.platform} over size budget: " + "; ".join(over)); sys.exit(1)
