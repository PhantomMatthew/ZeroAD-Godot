#!/bin/sh
set -e

: "${OS:=$(uname -s || true)}"
: "${MAKE:=make}"
: "${JOBS:=-j1}"
: "${TAR:=tar}"

cd "$(dirname "$0")"

PV=55fc4b2deac045ca06dc23d98426423356c507c1
LIB_VERSION=${PV}+wfg0

fetch()
{
	curl -fLo "premake-core-${PV}.tar.gz" \
		"https://github.com/premake/premake-core/archive/${PV}.tar.gz"
}

echo "Building Premake..."
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

if [ -e .already-built ] && [ "$(cat .already-built || true)" = "${LIB_VERSION}" ]; then
	echo "Skipping - already built (use --force-rebuild to override)"
	exit
fi

# fetch
if [ ! -e "premake-core-${PV}.tar.gz" ]; then
	fetch
fi

# unpack
rm -Rf "premake-core-${PV}"
"${TAR}" -xf "premake-core-${PV}.tar.gz"

# patch
# https://github.com/premake/premake-core/issues/2338
patch -d "premake-core-${PV}" -p1 <patches/0001-Make-clang-default-toolset-for-BSD.patch

#build
(
	cd "premake-core-${PV}"

	# Detection doesn't work on macOS, so specify manually.
	if [ "$OS" = "Darwin" ]; then
		arch=$(uname -m)
		if [ "$arch" = "arm64" ]; then
			platform="ARM64"
		elif [ "$arch" = "x86_64" ]; then
			platform="x64"
		fi
	fi

	config="--zlib-src=none --curl-src=none"

	case "${OS}" in
		Windows)
			${MAKE} "${JOBS}" -f Bootstrap.mak PREMAKE_OPTS="${config}" windows
			;;
		Darwin)
			${MAKE} "${JOBS}" -f Bootstrap.mak PLATFORM="${platform}" PREMAKE_OPTS="${config}" osx
			;;
		*BSD)
			${MAKE} "${JOBS}" -f Bootstrap.mak PREMAKE_OPTS="${config}" bsd
			;;
		*)
			${MAKE} "${JOBS}" -f Bootstrap.mak PREMAKE_OPTS="${config}" linux
			;;
	esac
)

# install
rm -Rf bin
mkdir -p bin
cp "premake-core-${PV}/bin/release/premake5" bin/

echo "${LIB_VERSION}" >.already-built
