FROM docker.io/library/postgres@sha256:4717f90960a225c2d1c5dac50508c401230f693ff626e06e84e6db0a68ded508 AS postgres-client
RUN mkdir /client && cp /usr/local/bin/psql /client/psql && \
    ldd /usr/local/bin/psql > /tmp/psql-libraries && \
    awk '$1 ~ /^\// { print $1 } $2 == "=>" && $3 ~ /^\// { print $3 }' /tmp/psql-libraries > /tmp/psql-paths && \
    while read -r path; do mkdir -p "/client$(dirname "$path")" && cp -L "$path" "/client$path" || exit 1; done < /tmp/psql-paths

FROM docker.io/library/golang@sha256:2173d702003b1a8d17b844191acaeb36e94de46f6cabf2cb27370e151bbce5aa AS build
WORKDIR /src
COPY main.go .
RUN CGO_ENABLED=0 go build -trimpath -buildvcs=false -o /web main.go

FROM scratch
COPY --from=postgres-client /client/ /
COPY --from=build /web /web
USER 65532:65532
EXPOSE 8080
ENTRYPOINT ["/web"]
