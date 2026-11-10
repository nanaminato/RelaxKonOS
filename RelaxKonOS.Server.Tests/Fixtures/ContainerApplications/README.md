# 容器应用平台首版验收夹具

使用隔离的 Linux x64 Server、专用本地 Docker Engine 和独立的 Server 发布目录。这里的镜像及目录清单仅用于验收。Web＋数据库夹具基于固定摘要的 PostgreSQL 17 Alpine，数据路径为 `/var/lib/postgresql/data`。ntfy 夹具从两个固定摘要的上游版本启动，并在同一路径启用 SQLite 消息缓存。 [PostgreSQL 官方镜像资料](https://github.com/docker-library/repo-info/blob/master/repos/postgres/remote/17-alpine.md) · [ntfy 安装文档](https://docs.ntfy.sh/install/) · [ntfy 发布记录](https://github.com/binwiederhier/ntfy/releases)

从本目录执行以下命令。将 `server_publish` 指向已有的隔离 Server 发布目录。保留 Registry 直到用例结束。

```sh
set -eu
server_publish=/absolute/path/to/isolated/server-publish
registry=127.0.0.1:15000
docker run --detach --name rk-ca-catalog-registry --publish "$registry:5000" \
  docker.io/library/registry@sha256:52b489ad8e6f40d1a676702e3ce398ec796ce38f08fbb786e1f324baa12d84b8

web="$registry/relaxkonos-ca/web-db:1.0.0"
ntfy_old="$registry/relaxkonos-ca/ntfy:2.37.0"
ntfy_new="$registry/relaxkonos-ca/ntfy:2.37.2"
docker build --platform linux/amd64 --provenance=false --tag "$web" WebDb
docker build --platform linux/amd64 --provenance=false --tag "$ntfy_old" \
  --build-arg NTFY_IMAGE=docker.io/binwiederhier/ntfy@sha256:5419ecbae3959a21d0ab1c59e826829c7d692262b23f5dc0f34171fbd96c5123 Ntfy
docker build --platform linux/amd64 --provenance=false --tag "$ntfy_new" \
  --build-arg NTFY_IMAGE=docker.io/binwiederhier/ntfy@sha256:e5d978c57b5a6a1095c8a74fd1cfe846dabef14369f2ff7a0d16456e727d40ec Ntfy

published_reference() {
  docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$1" |
    awk -v repository="${1%:*}" 'index($0, repository "@sha256:") == 1 { print }'
}
docker push "$web"
docker push "$ntfy_old"
docker push "$ntfy_new"
web_image=$(published_reference "$web")
old_image=$(published_reference "$ntfy_old")
new_image=$(published_reference "$ntfy_new")
sha256sum "$server_publish/RelaxKonOS.Server.dll"
python3 generate_catalog.py --server-publish "$server_publish" ntfy --version 2.37.0 --image "$old_image"
python3 generate_catalog.py --server-publish "$server_publish" ntfy --version 2.37.2 --image "$new_image" --predecessor-image "$old_image"
```

启动 Server，记录 Server 和客户端二进制哈希。从目录安装 `fixture-ntfy` 2.37.0，向唯一主题提交一条随机消息，再用相同主题轮询读回。按目录升级至 2.37.2，先读回原消息，再保留数据卸载并显式重装，最后再次读回原消息。每一步记录实例、修订、操作 ID 和实际镜像 ID。

```sh
ntfy_url=http://127.0.0.1:18081
topic=rk-ca-$(cat /proc/sys/kernel/random/uuid)
marker=$(cat /proc/sys/kernel/random/uuid)
curl -fsS --data-binary "$marker" "$ntfy_url/$topic"
curl -fsS "$ntfy_url/$topic/json?poll=1&since=all" |
  python3 -c 'import json,sys; assert any(json.loads(line).get("message") == sys.argv[1] for line in sys.stdin)' "$marker"
```

在新增包前记录目录中没有 `fixture-web-db`。停止隔离 Server，添加 Web＋数据库清单，再以相同二进制和启用的验收开关启动。重连同一个客户端并再次核对二进制哈希。使用目录字段设置独立的数据库密码。连接 Server 和客户端的完整验收按照平台设计第 7.1 节执行。 [第 7.1 节](../../../docs/platform/RelaxKonOS.ContainerApplications.Design.md#71-首版实机门槛)

```sh
python3 generate_catalog.py --server-publish "$server_publish" web-db --image "$web_image"
sha256sum "$server_publish/RelaxKonOS.Server.dll"
# 启动 Server 时设置 ApplicationDeployments__EnableManagedComposePreview=true
```

从目录安装 `fixture-web-db`，使用 Web 回环端口 `18083`。健康端点通过 PostgreSQL 连接执行查询，写入与读取均访问 PostgreSQL。停库时预期健康检查返回 503。恢复数据库并重建 Web 后，仅执行读请求复核原标记。

桌面重新登录后读取双服务验收能力。选择 `fixture-web-db`、填写实例名与回环端口，并点击「预览服务和存储」。确认 Web 回环发布、数据库只在项目内可访问且使用受管卷，然后填写目录要求的秘密并安装。调整端口时重新预览。

```sh
web_url=http://127.0.0.1:18083
marker=$(cat /proc/sys/kernel/random/uuid)
curl -fsS "$web_url/healthz"
curl -fsS -X PUT --data-binary "$marker" "$web_url/marker"
test "$(curl -fsS "$web_url/marker")" = "$marker"
```

结束后先保存脱敏证据，再按验收环境的资源清单清理专用 Engine 上的夹具、目录清单及 Registry。

## Server API 验收与证据

在专用 Linux x64 主机上运行 `verify_live.py`，它调用完整 Server 的目录与 Docker 路由。通过环境变量 `RK_CA_CONTROLLER_TOKEN` 和 `RK_CA_READER_TOKEN` 提供各自的短期会话令牌。将 `engine_id` 设置为预先核对的专用 Engine ID。运行前确保回环端口 18081 至 18084 空闲，证据文件必须为新文件。

```sh
python3 verify_live.py stateful --server http://127.0.0.1:8080 \
  --engine-id "$engine_id" --server-binary "$server_publish/RelaxKonOS.Server.dll" \
  --evidence stateful.jsonl
# 按上文添加 Web＋数据库清单，使用相同 Server 和客户端二进制重启并重连。
python3 verify_live.py web-db --server http://127.0.0.1:8080 \
  --engine-id "$engine_id" --server-binary "$server_publish/RelaxKonOS.Server.dll" \
  --evidence web-db.jsonl
```

两次记录的 Server 哈希与 Engine ID 应一致。另行保存桌面客户端安装前后的哈希及操作证据。脚本保留创建的夹具，供人工核对资源归属。强制终止窗口和原回执缺失注入须单独执行并记录。
