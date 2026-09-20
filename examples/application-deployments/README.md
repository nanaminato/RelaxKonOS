# 应用部署测试夹具（ApplicationDeployment fixtures）

这里的东西只有一个用途：**让「应用部署」功能可以被真跑一遍**。

对应文档：[Goal](./../../docs/applications/RelaxKonOS.ApplicationDeployment.Goal.md)、
[设计](./../../docs/applications/RelaxKonOS.ApplicationDeployment.Design.md)、
[实施进度](./../../docs/applications/RelaxKonOS.ApplicationDeployment.Progress.md)。

进度文档当前的结论是「T01–T15 全部跳过，原因：当前环境无 Docker」，因此该领域所有运行期行为
都还没有证据。本目录补上缺的那一半：四种部署来源各自的**真实可部署输入**，加上按模板判定逐条
镜像的离线复检，以及一个把 HTTP 协议按顺序打一遍的驱动脚本。

> 本目录**不改变**进度文档的结论。它提供的是执行测试所需的输入与步骤，不等于任何一项已验收。

## 目录结构

```
examples/application-deployments/
├── README.md                                 本文件
├── .gitignore                                忽略构建脚本的临时目录（dist/ 刻意不忽略）
├── fixtures/
│   ├── java-http/                            可执行 JAR（Java 21 字节码 + Main-Class）
│   │   ├── src/App.java                      JDK 内置 HttpServer，仅 / 与 /healthz 返回 200
│   │   ├── build.sh
│   │   ├── dist/relaxkonos-ad-java-http.jar  裸 JAR（仅本地冒烟用，不要上传）
│   │   └── dist/relaxkonos-ad-java-http.zip  ← 上传这个（外层 ZIP 内含那个 JAR）
│   ├── dotnet-web/                           ASP.NET Core Minimal API，框架依赖发布
│   ├── dotnet-worker/                        控制台 Worker，框架依赖发布
│   ├── python-web/                           Flask + 锁定依赖（flask==3.1.0）
│   ├── python-worker/                        仅标准库、requirements.txt 只含注释（构建不访问包索引）
│   ├── image/README.md                       现成镜像用例的参数与负向用例
│   └── negative/                             7 个刻意无效的归档，每个只在一处不合法
└── scripts/
    ├── build-fixtures.sh / .ps1              重建全部制品并复检
    ├── build-negative-fixtures.py            生成 negative/ 下的归档
    ├── pack.py                               确定性打包（固定时间戳、固定顺序）
    ├── verify-fixtures.py                    离线复检：镜像服务端模板判定
    └── run-deployment-flow.ps1               端到端驱动：上传 → 创建 → 部署 → 轮询 → 观测 → 回滚 → 启停 → 删除
```

## 六个正向用例：向导该填什么

端口按用例分配，互不冲突。`hostPort` **必须**给（除非 `readinessLevel=Process`），否则 HTTP
就绪检查无处可查。

| 用例 | 应用名 | 来源 | 工作负载 | 就绪级别 | 健康检查路径 | 容器端口 | 宿主端口 | 上传的归档 | 其余输入 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| java-http | `demo-java-http` | `JavaJar` | `Web` | `Http` | `/healthz` | 8080 | 18081 | `fixtures/java-http/dist/relaxkonos-ad-java-http.zip` | 参数 `--port=8080` |
| dotnet-web | `demo-dotnet-web` | `DotNetPublish` | `Web` | `Http` | `/healthz` | 8080 | 18082 | `fixtures/dotnet-web/dist/relaxkonos-ad-dotnet-web.zip` | `selfContained=false` |
| dotnet-worker | `demo-dotnet-worker` | `DotNetPublish` | `Worker` | `Process` | — | 8080 | — | `fixtures/dotnet-worker/dist/relaxkonos-ad-dotnet-worker.zip` | `selfContained=false` |
| python-web | `demo-python-web` | `PythonProject` | `Web` | `Http` | `/healthz` | 8080 | 18083 | `fixtures/python-web/dist/relaxkonos-ad-python-web.zip` | 入口模块 `app` |
| python-worker | `demo-python-worker` | `PythonProject` | `Worker` | `Process` | — | 8080 | — | `fixtures/python-worker/dist/relaxkonos-ad-python-worker.zip` | 入口模块 `worker`；配置 `HEARTBEAT_SECONDS=2` |
| image-nginx | `demo-image-nginx` | `Image` | `Web` | `Http` | `/` | 80 | 18084 | 无 | `imageReference=nginx:1.29.0-alpine` |

三个容易踩的点：

1. **Java 上传的是 ZIP，不是 JAR。** `JavaJarTemplate` 先解压归档再在解压结果里找 `*.jar`，
   并要求**恰好一个**且清单里有 `Main-Class`。直接把 `.jar` 传上去会得到
   `archive_content_invalid`（一个 `*.jar` 都找不到）。
2. **端口要跟应用实际监听的一致。** 模板只设置 `EXPOSE`，不注入端口变量：Java 从
   `--port=` 参数读、.NET 从模板写好的 `ASPNETCORE_URLS` 读、Python 从 `PORT` 环境变量读
   （默认 8080）。所以 Python 用例若把 `containerPort` 改成 8080 以外的值，要加一条非机密
   配置 `PORT=<值>`。
3. **`/healthz` 之外的路径一律 404。** 三个 Web 夹具都刻意只在 `/` 与 `/healthz` 上返回 200，
   这样把 `healthCheckPath` 填错时部署会真的失败，而不是被「什么路径都 200」掩盖。

## 怎么跑

### 前置条件

- **Docker Engine 跑 Linux 容器。** 模板只声明支持 `linux/amd64`、`linux/arm64`、`linux/arm`；
  Windows 容器不在范围内。
- **Server 处于系统模式，操作用户是 `controller`。** 本领域组合 Docker Engine，因此继承 Docker
  边界：**User Mode 宿主不提供该能力**（`Supports(ApplicationDeployments) == false`），客户端会
  表现为「服务器未提供该接口」。
- 宿主端口 18081–18084 未被占用。

### 方式一：客户端向导

按上一节的表逐项填写。向导只提交**定义**，部署是随后的独立动作，所以「创建成功」不代表已发布。

### 方式二：脚本驱动（可留证据）

```powershell
# 单用例
.\scripts\run-deployment-flow.ps1 -ServerUrl http://127.0.0.1:5000 -Identifier <账号> -Password <密码> -Case java-http

# 完整一遍：部署 → 同幂等键重放 → 回滚 → 停止 → 启动 → 删除
.\scripts\run-deployment-flow.ps1 -ServerUrl http://127.0.0.1:5000 -Identifier <账号> -Password <密码> `
    -Case dotnet-web -TestIdempotency -Rollback -Lifecycle -Delete
```

脚本按协议要求，为每个变更请求带 `Idempotency-Key`、为部署请求带 `confirmed=true`，并把长操作
的 `stage` / `state` / `progress` 变化打出来。`-TestIdempotency` 会用**同一个键**重放部署并核对
返回的是不是同一个 operation，用来验证「同键同请求不会发第二个版本」。

若 Server 就跑在同一台机器上，加 `-UseFileReference` 可改为调用
`POST /file-references` 登记本地文件，省掉一次上传：

```powershell
.\scripts\run-deployment-flow.ps1 ... -Case python-web -UseFileReference
```

想手工发请求，注意两点：上传端点的 multipart 字段名是 `file`；上传结果只返回
`referenceId`，宿主路径**不会**进入部署请求。

### 负向用例

每个归档只在一处不合法，失败原因因此可以唯一归因。

| 归档 | 用例 | 预期问题码 |
| --- | --- | --- |
| `negative/java-two-jars.zip` | 一次部署两个 JAR | `archive_content_invalid` |
| `negative/java-no-main-class.zip` | JAR 清单没有 `Main-Class` | `entry_point_invalid` |
| `negative/python-unpinned.zip` | `requirements.txt` 未锁定版本 | `archive_content_invalid` |
| `negative/dotnet-windows-rid.zip` | 发布目标是 `win-x64` | `image_platform_mismatch` |
| `negative/archive-traversal.zip` | 条目名为 `escape/../escape.txt` | `archive_unsafe_entry` |
| `negative/archive-absolute-entry.zip` | 条目名为 `/etc/...` | `archive_unsafe_entry` |
| `negative/archive-symlink.zip` | 条目是符号链接（mode `0xA000`） | `archive_unsafe_entry` |

除归档之外，`verify-fixtures.py` 还把这几类请求级负向用例纳入断言：`nginx:latest`
（`image_reference_invalid`）、Image 模板里混入 `baseImage` 或 `programEntry`（`invalid_request`）、
给归档来源额外传 `imageReference`（`invalid_request`）、把 .NET Web 包按 Worker 部署
（`runtime_mismatch`）、以及镜像用例中「无标签引用**不会**被拦」这一实现现状（见
[`fixtures/image/README.md`](./fixtures/image/README.md)）。

## 重建与自检

```sh
scripts/build-fixtures.sh          # Linux / macOS / Git Bash
scripts/build-fixtures.ps1         # Windows PowerShell 5.1+
```

需要 JDK（`javac`/`jar`）、.NET SDK、Python 3。归档由 `pack.py` 写出，时间戳与条目顺序固定，
所以同一输入重复构建得到同一份归档——机制与实测见下一节。

`scripts/verify-fixtures.py` 不需要 Docker，也不需要 Server：它把
`ApplicationArchiveSafety.ExtractAsync`、`PublishRoot` 以及三个模板的
`PrepareBuildContextAsync` 判定顺序逐条重写一遍，然后断言 6 个正向用例全部通过、15 个负向用例
各自命中预期问题码。任何一个用例为「因意外原因失败」都会让它报 MISMATCH——这一点是刻意的，
因为一个归因错误的夹具比没有夹具更糟。

## 体积与版本控制

**结论：可以直接进版本库，体积上毫无负担。** 36 个文件合计约 127 KiB，其中 13 个归档合计
约 40 KiB。最大的单个文件是 `scripts/run-deployment-flow.ps1`（19.8 KB，纯文本），最大的二进制
是 `fixtures/dotnet-web/dist/relaxkonos-ad-dotnet-web.zip`（17.2 KB）。作为参照，仓库 `.git` 本身
是 39 MB，这批夹具占它约 0.3%；GitHub 的单文件告警阈值是 50 MB，最大的那个归档只有它的
约 1/3000。

体积不是取舍点，取舍在别处：这些归档**是测试输入**，不是可以随手丢掉的构建输出——上传单元就是
`dist/*.zip` 本身，而 `negative/` 下那 7 个刻意无效的归档连编译器都产不出来。所以两者都进版本库。
仓库目前没有任何 `.zip`/`.jar` 被跟踪，本目录是刻意的例外。

| 路径 | 进版本库 | 原因 |
| --- | --- | --- |
| `fixtures/*/src/`、`*/build.sh` | 是 | 归档的来源 |
| `fixtures/*/dist/*.zip`、`*.jar` | 是 | 上传单元本身，也是下面证据表引用的确切字节 |
| `fixtures/negative/*.zip` | 是 | 手工构造的对抗性输入，无法由编译器产出 |
| `fixtures/*/src/*/bin/`、`obj/` | 否 | 仓库根 `.gitignore` 的 `[Bb]in/`、`[Oo]bj/` |
| `fixtures/*/build/`、`stage/`、`.publish/` | 否 | 本目录 `.gitignore`；各 `build.sh` 成功结束时也会自删 |

**重建不会产生伪差异。** 四个来源都逐位可复现：

- `javac` 对同一源码两次编译产出的 `App.class` 逐位相同（实测 sha256 一致）。
- `jar --create` 默认把源文件 mtime 写成条目时间戳，所以 `build.sh` 显式传
  `--date=1980-01-01T00:00:02Z`。该值是 `jar` 接受的最小值——ZIP/DOS 时间格式表示不了那两秒，
  更早的时间会被直接拒绝。实测：不传 `--date` 的两次构建哈希不同，传了之后完全一致。
- `pack.py` 固定条目顺序与时间戳（`FIXED_TIMESTAMP = 1980-01-01T00:00:00`）。
- `dotnet publish` 在 .NET SDK 10.0.300 上两次发布的 5 个文件逐位相同（实测）。

> 可复现的前提是工具链版本一致：换 JDK 或 .NET SDK 版本会改变产物字节。夹具是用来验证服务端行为
> 的，不是验证编译器的，所以这不算问题——但它意味着重建后若出现 diff，先看工具链版本，别急着
> 认定夹具写坏了。

## 已核实的证据（本仓库内可复核）

| 日期 | 内容 | 命令 | 结果 |
| --- | --- | --- | --- |
| 2026-09-20 | 四个模板的制品全部构建成功 | `javac --release 21 -d build src/App.java` + `jar --create --file ... --main-class App`；`dotnet publish <csproj> -c Release --nologo -o <dir>`；`python scripts/pack.py <src> <zip> <prefix>` | JDK 25 编译出 Java 21 字节码（class file 65.0，清单含 `Main-Class: App`）；.NET SDK 10.0.300 发布 `net10.0` 框架依赖包，`runtimeconfig.json` 的 `tfm`/`frameworks` 与模板判定一致 |
| 2026-09-20 | 预检判定逐条复检 | `python scripts/verify-fixtures.py` | 21/21 用例行为与声明一致 |
| 2026-09-20 | 归档逐位可复现 | 连续两次 `sh fixtures/java-http/build.sh`；连续两次 `dotnet publish <csproj> -c Release --nologo -o <dir>` | 修复前：Java 归档两次构建 sha256 不同（`jar` 把 mtime 写进条目）；加上 `--date` 后 JAR 与归档两次构建哈希一致。.NET 发布的 5 个文件两次构建逐一比对全部相同 |
| 2026-09-20 | Java 制品真实运行 | `java -jar dist/relaxkonos-ad-java-http.jar --port=18081` | `/healthz` → 200，`/nope` → 404 |
| 2026-09-20 | .NET Web 制品真实运行 | `ASPNETCORE_URLS=http://127.0.0.1:18082 dotnet DotNetWebDemo.dll` | `/healthz` → 200，`/nope` → 404 |

> 上表第二列的 .NET 步骤是在开发沙箱内以等效命令单独执行的：该沙箱会剥离子进程的 `TMP`/`TEMP`，
> MSBuild 因此回退到 Windows 目录建临时文件并以 `MSB1025` 失败。这是宿主环境问题，不是脚本问题；
> 在常规宿主上直接跑 `scripts/build-fixtures.sh` / `build-fixtures.ps1` 即可，两者的构建命令与上表一致。

**这些证据只覆盖「输入合法」与「程序本身能起来」。** 下面这些仍然完全未验证，因为本机没有跑
Server、也没有执行任何部署：

- 镜像拉取与构建、容器创建、候选改名、停机替换、失败恢复。
- 就绪检查在容器网络下的行为（宿主经 `127.0.0.1:{hostPort}` 访问容器）。
- 回滚、取消、Server 重启后的启动核对。
- 反向代理站点写入与校验失败回退。
- 客户端向导与三语界面呈现。

要在真实 Engine 上留下证据，请按
[进度文档](./../../docs/applications/RelaxKonOS.ApplicationDeployment.Progress.md)的「环境与验证
记录」格式补登：环境 ID、发行版/架构、Engine 版本、宿主是否装过 Java/.NET/Python 工具链、命令与
结果。**编译通过与预检通过都不构成任何 T 项的通过。**

## 状态存放位置

Server 侧的账本、暂存、构建上下文与机密物化文件都在 `ApplicationDeploymentOptions.RootDirectory`
下（默认 `RelaxKonOS.Server/data/application-deployments`，相对 ContentRoot）：

| 路径 | 内容 |
| --- | --- |
| `catalog.json` | 应用与修订账本 |
| `operations.json` | 操作条目与审计 |
| `secrets.json` | 机密密文（DataProtection 保护） |
| `staging/` | 上传暂存（默认 20 分钟过期） |
| `build/` | 每个输入指纹一份构建上下文（最多保留 20 份） |
| `mounts/{appId}/{revisionId}/` | 该修订的机密物化文件 |

清空测试数据时请整体删掉该目录，不要只删其中一个账本：三个账本都是 fail-closed，缺一个会让
后续所有变更返回 `application-deployment.store_unavailable`（503）。
