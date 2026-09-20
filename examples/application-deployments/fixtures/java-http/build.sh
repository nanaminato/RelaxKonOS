#!/usr/bin/env sh
# Builds the Java fixture:
#   dist/relaxkonos-ad-java-http.jar  - the executable JAR itself
#   dist/relaxkonos-ad-java-http.zip  - the archive that is actually uploaded for deployment
#
# The upload must be a ZIP that *contains* the JAR, not the bare JAR: the JavaJar template extracts
# the archive, then requires exactly one *.jar inside it with a manifest Main-Class. Uploading a bare
# JAR would find zero *.jar entries and fail with application-deployment.archive_content_invalid.
#
# The JAR targets the Java 21 class-file level so it runs on the template's default base image
# (eclipse-temurin:21-jre). Only the JDK is used here; the deployment itself never calls the host
# JDK, because the container image supplies the JRE.
#
# The JAR is built reproducibly: `jar` otherwise stamps every entry with the source file's mtime, so
# two builds of identical sources would differ and every rebuild would show up as a spurious diff.
# `--date` pins the entries instead. 1980-01-01T00:00:02Z is the ZIP epoch plus the two seconds the
# ZIP/DOS timestamp format cannot represent; `jar` rejects anything earlier.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$here/../.." && pwd)
javac_bin=${JAVAC:-javac}
jar_bin=${JAR:-jar}
python_bin=${PYTHON:-python3}
jar_epoch=1980-01-01T00:00:02Z

rm -rf "$here/build" "$here/stage" "$here/dist"
mkdir -p "$here/build" "$here/stage" "$here/dist"

"$javac_bin" --release 21 -Xlint:all -d "$here/build" "$here/src/App.java"
"$jar_bin" --create --date="$jar_epoch" --file "$here/dist/relaxkonos-ad-java-http.jar" \
  --main-class App -C "$here/build" .

cp "$here/dist/relaxkonos-ad-java-http.jar" "$here/stage/"
"$python_bin" "$root/scripts/pack.py" "$here/stage" \
  "$here/dist/relaxkonos-ad-java-http.zip" relaxkonos-ad-java-http

# The staging directory would otherwise contribute a second *.jar path to the archive if it were
# packed by mistake, so it never outlives the build.
rm -rf "$here/build" "$here/stage"
echo "built $here/dist/relaxkonos-ad-java-http.jar and $here/dist/relaxkonos-ad-java-http.zip"
