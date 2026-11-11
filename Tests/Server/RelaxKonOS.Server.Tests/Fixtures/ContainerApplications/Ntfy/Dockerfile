ARG NTFY_IMAGE=docker.io/binwiederhier/ntfy@sha256:5419ecbae3959a21d0ab1c59e826829c7d692262b23f5dc0f34171fbd96c5123
FROM ${NTFY_IMAGE}
ENV NTFY_CACHE_FILE=/var/cache/ntfy/cache.db
ENTRYPOINT ["ntfy", "serve"]
CMD []
