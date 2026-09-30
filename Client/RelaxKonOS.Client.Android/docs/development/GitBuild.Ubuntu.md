# AD06 Ubuntu Git 构建执行边界

> 当前 Ubuntu 构建环境要求。执行证据与未关闭检查统一见 [当前状态](../status/Progress.md) 和 [验收清单](../status/Verification.md)。

## 宿主准备

AD06 使用 Ubuntu 上独立、预先配置的 rootless BuildKit 容器。它通过 Buildx `remote` driver 的 `docker-container://` 端点接入，和 AD02 使用的普通 Docker build 分开。服务器不会自动创建 builder，也不会在无配额时退回默认 builder。

运维应使用固定版本或 digest 的 `moby/buildkit:*rootless` 镜像创建独立容器，设置内存、CPU 和 PID 上限；容器不能以 root 用户或 `--privileged` 运行，不能绑定宿主目录或 Docker socket。容器接入专用的 `ad06-egress` 网络，该网络的宿主防火墙规则需限制出站访问，只允许业务所需的 Git 和镜像/包源。不允许 BuildKit 的 `network.host`、`security.insecure` 等特权 entitlement。rootless BuildKit 可能需要额外的 seccomp/AppArmor 例外，应由宿主配置评估并固定，不能简单开放容器特权。

以下命令只展示接口关系，镜像 digest、网络规则和安全选项应由宿主部署方案确定：

```sh
docker run -d --name relaxkonos-buildkit \
  --memory 2g --cpus 2 --pids-limit 512 \
  --network ad06-egress \
  <pinned-rootless-buildkit-image>
docker buildx create --name relaxkonos-ad06 --driver remote \
  docker-container://relaxkonos-buildkit
```

服务器配置示例：

```json
{
  "GitBuilds": {
    "BuilderName": "relaxkonos-ad06",
    "BuilderContainer": "relaxkonos-buildkit",
    "BuilderNetwork": "ad06-egress",
    "AllowedHosts": ["github.com", "gitlab.com"],
    "MaximumMemoryBytes": 2147483648,
    "MaximumCpuCores": 2,
    "MaximumPids": 512,
    "TimeoutMinutes": 20
  }
}
```

提交任务前与执行时，服务器核对 Ubuntu、`remote` driver 的目标容器、容器运行状态、非 root 用户、非特权模式、无宿主 bind/附加 capability、指定网络和全部三种资源上限。不符合配置返回 `git-build.builder-*`，不启动默认 Docker builder。服务器本身还必须能调用 `git`、`docker buildx` 和本机 Docker Engine；构建结束用 `--load` 导入镜像供 AD02 发布。

## 协议和任务

`/api/v1.0/git/builds` 下的 API 提供凭据引用、分支/标签列表、SHA 解析、构建任务列表/详情/取消。写操作按现有应用部署管理权限授权；数据按登录用户隔离。提交构建要求 8–128 字符幂等键，记录仓库 URL、引用、完整 SHA、上下文目录、Dockerfile、状态、问题码、有界日志、镜像 tag/ID。列表只显示最近 200 项；旧任务去除日志后保留元数据和幂等键。服务重启时排队和运行中的任务标为 `interrupted`，同键重试只返回原记录，不会暗中重复构建。取消仅终止当前构建进程，不触碰旧应用。

仓库地址仅接受配置白名单中的 HTTPS `*.git` 地址，不接受 URL 内的账号、token、查询或片段；短分支名映射到 `refs/heads/`，标签需明确 `refs/tags/`。预览与提交时分别解析引用，提交 SHA 不一致即拒绝。接单后远端分支移动不改变该任务：BuildKit 用完整 SHA 加 `checksum` 读取上下文。尚未推送到远端的本地提交不能构建，手机保存、提交、推送仍是独立动作。

私有仓库首版只支持 GitHub HTTPS token。token 由服务器 Data Protection 加密保存，API 只返回名称和不透明 ID；Git 查询使用进程环境配置的认证头，BuildKit 使用限定到 `github.com` 的预取 secret。token 不进入 URL、构建参数、构建账本、镜像标签或普通草稿。其他私有 Git 服务、SSH key 和私有依赖认证明确不支持；公开 GitLab 仓库可用。构建日志每行截为 512 字符，仅保留最近 120 行，已知 token 和 Basic 头编码会替换为 `[redacted]`。

Dockerfile 与上下文目录只接受仓库内相对路径；`subdir` 使 BuildKit 的上下文限定为该目录。BuildKit Git 上下文默认递归包含子模块，因此首版子模块仅在隔离构建网络允许的公开来源可工作，私有子模块没有独立凭据支持。Git LFS 没有被此流程展开；需要 LFS 对象的项目不属于首版可发布范围。运维需要通过专用网络阻断不允许的额外 Git/HTTP 来源；服务器的主仓库白名单不等同于 Dockerfile `RUN`/`ADD` 的出站策略。

构建成功的记录保存实际镜像 ID。Android 另行创建或选取 AD02 的 `Image` 应用并提交 `gitBuildId` 与镜像 tag。部署预检要求构建任务属于当前用户、处于成功状态、tag 和当前本机镜像 ID 与记录一致；已发布修订额外保存构建 ID、仓库、引用、SHA、上下文目录和 Dockerfile。创建容器使用镜像 ID，回滚沿用该修订的镜像与配置，不重构建旧分支。发布失败不会替换运行中的旧版本。

rootless Builder、私有认证、子模块/LFS 拒绝、脱敏及磁盘错误的验证要求见集中验收清单。成功构建镜像未自动清理，因为可能仍被发布修订引用；部署前应预留独立磁盘空间，后续按修订引用实现安全保留/回收。
