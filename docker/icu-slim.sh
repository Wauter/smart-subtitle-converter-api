#!/bin/sh
# Builds the ICU + tzdata overlay that turns plain `aspnet:*-noble-chiseled` into a full-globalization
# image without the weight of `-chiseled-extra` (design §13).
#
#   in:  /src  = the target-arch chiseled-extra rootfs pieces (usr/lib, usr/share/zoneinfo, usr/share/doc)
#   out: /out  = files to COPY onto plain chiseled at /
#
# libicuuc/libicui18n are copied byte-for-byte from chiseled-extra (same Ubuntu package, same patches).
# libicudata is rebuilt with the ICU data items .NET never reads removed:
#   charset converters (*.cnv, cnvalias)  .NET has its own encodings; CodePagesEncodingProvider is managed
#   translit/, rbnf/, unit/               no .NET API uses transliteration, spelled-out numbers or unit names
#   unames.icu, confusables.cfu           character names / spoof checking: not used by .NET
#   brkitr/*.dict                         CJK/Thai/Lao/Khmer/Burmese word dictionaries; .NET only uses
#                                         character (grapheme) break rules, which are kept
# Everything else stays: all locales, collation tailorings, casing/normalization (uprops, ucase, *.nrm),
# currency/language/region/zone display names, calendars, time zone rules. The app's startup
# GlobalizationCheck (/readyz) verifies this on every boot, so a bad trim fails the smoke test.
#
# Runs on the build host; the target-arch library is produced with the target binutils, so no QEMU.
set -eu
: "${TARGETARCH:?}"
case "$TARGETARCH" in
  amd64) T=x86_64-linux-gnu ;;
  arm64) T=aarch64-linux-gnu ;;
  *) echo "unsupported TARGETARCH $TARGETARCH" >&2; exit 1 ;;
esac
LIB=/src/usr/lib/$T
READELF="$T-readelf"; LD="$T-ld"
for tool in "$READELF" "$LD" icupkg genccode; do command -v "$tool" >/dev/null || { echo "missing tool: $tool" >&2; exit 1; }; done
UC=$(ls "$LIB"/libicuuc.so.*.* | head -1)
VER=${UC##*.so.}          # e.g. 74.2
MAJ=${VER%%.*}            # e.g. 74
TOOLS=$(dpkg-query -W -f '${Version}' icu-devtools)
[ "${TOOLS%%.*}" = "$MAJ" ] || { echo "icu-devtools $TOOLS does not match libicu $VER in the base image" >&2; exit 1; }

work=$(mktemp -d); cd "$work"

# 1. Pull the ICU common data blob (symbol icudt<maj>_dat) out of the shipped libicudata.
DATA="$LIB/libicudata.so.$VER"
set -- $("$READELF" -Ws --dyn-syms "$DATA" | awk -v s="icudt${MAJ}_dat" '$8==s {print $2, $3, $7; exit}')
[ $# -eq 3 ] || { echo "icudt${MAJ}_dat not found in $DATA" >&2; exit 1; }
VADDR=$((0x$1)); SIZE=$(( $2 )); NDX=$3
set -- $("$READELF" -SW "$DATA" | sed 's/\[ */[/' | awk -v n="[$NDX]" '$1==n {print $4, $5; exit}')
OFF=$(( VADDR - 0x$1 + 0x$2 ))
mkdir pkg && dd if="$DATA" of="pkg/icudt${MAJ}l.dat" bs=1M iflag=skip_bytes,count_bytes skip="$OFF" count="$SIZE" status=none

# 2. Drop what .NET never reads.
cd pkg
icupkg -l "icudt${MAJ}l.dat" > all.txt
grep -E '\.cnv$|^cnvalias\.icu$|^translit/|^rbnf/|^unit/|^unames\.icu$|^confusables\.cfu$|^brkitr/.*\.dict$' all.txt > drop.txt
icupkg --ignore-deps -r drop.txt "icudt${MAJ}l.dat"
echo "icu $VER: kept $(($(wc -l < all.txt) - $(wc -l < drop.txt))) of $(wc -l < all.txt) items, data $SIZE -> $(stat -c %s "icudt${MAJ}l.dat") bytes"

# 3. Re-wrap as a target-arch shared library exporting the same symbol and soname.
genccode -e "icudt${MAJ}" -o -m "$UC" "icudt${MAJ}l.dat" >/dev/null
"$LD" -shared -z noexecstack -z relro -soname "libicudata.so.$MAJ" -o "libicudata.so.$VER" "icudt${MAJ}l_dat.o"

# 4. Assemble the overlay.
O=/out/usr/lib/$T; mkdir -p "$O" /out/usr/share/doc
cp -P "$LIB/libicuuc.so.$MAJ" "$LIB/libicuuc.so.$VER" "$LIB/libicui18n.so.$MAJ" "$LIB/libicui18n.so.$VER" \
      "$LIB/libicudata.so.$MAJ" "$O/"
install -m 0644 "libicudata.so.$VER" "$O/"
# tzdata without the leap-second "right/" duplicates (.NET reads the plain zone files).
mkdir -p /out/usr/share/zoneinfo
(cd /src/usr/share/zoneinfo && tar cf - --exclude=./right .) | (cd /out/usr/share/zoneinfo && tar xf -)
cp -a /src/usr/share/doc/libicu$MAJ /src/usr/share/doc/tzdata /out/usr/share/doc/
du -sh /out
