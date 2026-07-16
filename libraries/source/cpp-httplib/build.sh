#!/bin/sh
set -e

: "${TAR:=tar}"

cd "$(dirname "$0")"

PV=0.30.1
LIB_VERSION=${PV}+wfg0

fetch()
{
	curl -fLo "cpp-httplib-${PV}.tar.gz" \
		"https://github.com/yhirose/cpp-httplib/archive/refs/tags/v${PV}.tar.gz"
}

while [ "$#" -gt 0 ]; do
	case "$1" in
		--fetch-only)
			fetch
			exit
			;;
		--force-rebuild) rm -f .already-built ;;
		*)
			echo "Unknown option: $1"
			exit 1
			;;
	esac
	shift
done

echo "Building cpp-httplib..."
if [ -e .already-built ] && [ "$(cat .already-built || true)" = "${LIB_VERSION}" ]; then
	echo "Skipping - already built (use --force-rebuild to override)"
	exit
fi

# fetch
if [ ! -e "cpp-httplib-${PV}.tar.gz" ]; then
	fetch
fi

# unpack
rm -Rf "cpp-httplib-${PV}"
"${TAR}" -xf "cpp-httplib-${PV}.tar.gz"

# configure
rm -Rf build
cmake -B build -S "cpp-httplib-${PV}" \
	-DBUILD_SHARED_LIBS=NO \
	-DCMAKE_BUILD_TYPE=Release \
	-DCMAKE_INSTALL_LIBDIR=lib \
	-DCMAKE_INSTALL_PREFIX="$(realpath .)" \
	-DHTTPLIB_COMPILE=YES \
	-DHTTPLIB_REQUIRE_BROTLI=NO \
	-DHTTPLIB_REQUIRE_OPENSSL=NO \
	-DHTTPLIB_REQUIRE_ZLIB=NO \
	-DHTTPLIB_USE_BROTLI_IF_AVAILABLE=NO \
	-DHTTPLIB_USE_OPENSSL_IF_AVAILABLE=NO \
	-DHTTPLIB_USE_ZLIB_IF_AVAILABLE=NO \
	-DHTTPLIB_USE_ZSTD_IF_AVAILABLE=NO

# build
cmake --build build

# install
rm -Rf include lib share
cmake --install build

echo "${LIB_VERSION}" >.already-built
