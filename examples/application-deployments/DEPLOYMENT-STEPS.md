# 六个正向用例的完整部署步骤（客户端向导）

本文覆盖 `fixtures/` 下**全部六个正向用例**，每个都是一份可逐格照抄的操作步骤：从「新建部署」点到最后验证通过。
每一步都给出字段的界面原文、要填的值、以及填错时会看到的**原文报错**。

| # | 用例 | 来源 | 工作负载 | 宿主端口 | 归档 |
| --- | --- | --- | --- | --- | --- |
| 1 | [demo-java-http](#1-demo-java-httpjava-jar) | Java JAR | Web 服务 | 18081 | `java-http/dist/relaxkonos-ad-java-http.zip` |
| 2 | [demo-dotnet-web](#2-demo-dotnet-webnet-发布) | .NET 发布 | Web 服务 | 18082 | `dotnet-web/dist/relaxkonos-ad-dotnet-web.zip` |
| 3 | [demo-dotnet-worker](#3-demo-dotnet-worker后台工作进程) | .NET 发布 | 后台工作进程 | 无 | `dotnet-worker/dist/relaxkonos-ad-dotnet-worker.zip` |
| 4 | [demo-python-web](#4-demo-python-webpython-项目) | Python 项目 | Web 服务 | 18083 | `python-web/dist/relaxkonos-ad-python-web.zip` |
| 5 | [demo-python-worker](#5-demo-python-worker后台工作进程) | Python 项目 | 后台工作进程 | 无 | `python-worker/dist/relaxkonos-ad-python-worker.zip` |
| 6 | [demo-image-nginx](#6-demo-image-nginx容器镜像) | 容器镜像 | Web 服务 | 18084 | 无（现成镜像） |

> 本文描述的是**操作步骤**，不是验收结论。按本文跑通只能证明「这条链路在这台机器上走通了」，
> 不能替代 [进度文档](../../docs/applications/RelaxKonOS.ApplicationDeployment.Progress.md)
> 里 T01–T15 的验收记录。

---

## 共用前提（六者相同）

### 五项前置检查

| 检查 | 怎么看 | 不满足时的表现 |
| --- | --- | --- |
| Server 处于系统模式 | 客户端能看到「应用部署」并加载出应用列表 | 「此服务器未提供应用部署接口。」/「服务器未提供"应用部署"接口。」 |
| 当前账号有管理权限 | `新建部署` 按钮可点 | 按钮灰掉，或「此账户无权执行该操作。」 |
| Docker Engine 跑 Linux 容器 | 宿主机执行 `docker version` | 「容器引擎不可用。」/「服务器上未安装容器引擎。」 |
| 宿主能拉到基础镜像 | `docker pull eclipse-temurin:21-jre`、`docker pull nginx:1.29.0-alpine` 能成功 | 「构建镜像失败。」/「找不到该镜像。」——见下 |
| 宿主端口空闲 | `netstat -ano \| findstr 18081` 无结果 | 「宿主端口已被占用。」 |

本领域**继承 Docker 边界**：User Mode 宿主不提供该能力（`Supports(ApplicationDeployments) == false`），
客户端表现为「服务器未提供该接口」——这不是版本问题，换系统模式的宿主即可。

端口按用例分配，互不冲突：Web 用例占 18081–18084；两个 Worker 用例**不给宿主端口**。

### 基础镜像必须拉得下来

用例 1–5 都是**在部署时现场构建镜像**，构建上下文里的 `Dockerfile` 第一行是 `FROM <基础镜像>`。
默认值分属两个不同的仓库，得分开测：

| 用例 | 默认基础镜像 | 仓库 |
| --- | --- | --- |
| 1 Java | `eclipse-temurin:21-jre` | Docker Hub |
| 2、3 .NET | `mcr.microsoft.com/dotnet/aspnet:10.0` / `runtime:10.0` | **MCR（不是 Docker Hub）** |
| 4、5 Python | `python:3.12-slim` | Docker Hub |
| 6 镜像 | `nginx:1.29.0-alpine` | Docker Hub |

这一行由 Docker 守护进程自己去拉，**不经过本项目的镜像源设置**——`IDockerImageMirrorResolver`
只作用于 Docker 管理器自己的「拉取镜像」端点。另外用例 4 的构建期还会 `pip install`（`flask==3.1.0`），
所以它还要能访问 PyPI；用例 5 的 `requirements.txt` 没有依赖，不需要。

先自测：

```bash
docker manifest inspect eclipse-temurin:21-jre
docker manifest inspect python:3.12-slim
docker manifest inspect nginx:1.29.0-alpine
```

若报 `registry-1.docker.io/v2/: Bad Gateway` 或超时，说明 Docker Hub 不通。三种绕法：

1. **给 Docker 守护进程配镜像源**（推荐，Docker Hub 那几条一次配好）：在 Docker Desktop 的
   `daemon.json` 里加 `"registry-mirrors": ["https://<你的镜像源>"]` 后重启引擎。
2. **改第 2 步的「基础镜像」**：填成可达镜像源上的**同一个镜像**，例如
   `docker.m.daocloud.io/library/eclipse-temurin:21-jre`，同时**把「运行时版本」留空**。
   留空时服务端不做 `:{版本}` 一致性检查（只要求不带 `latest`），所以不会撞 `runtime_mismatch`。
3. **先只跑 .NET 用例**：MCR 与 Docker Hub 是两套独立的基础设施，Docker Hub 不通时
   `mcr.microsoft.com` 往往仍然可达，用例 2、3 因此可能是六个里唯一能直接跑起来的。

换基础镜像时有一个硬约束：三种来源生成的 Dockerfile 都会执行
`RUN useradd --system --uid 10001 --create-home --shell /usr/sbin/nologin appuser`。
所以替代镜像必须带 `useradd`——Debian / Ubuntu 系（`-jre`、`-slim`、`aspnet`）都有，
Alpine 系没有，换过去会在构建期报 `useradd: not found`。

### 第 2 步的字段可见性（先看这个，避免「我的界面上没有这一格」）

| 字段 | Java JAR | .NET 发布 | Python 项目 | 容器镜像 |
| --- | --- | --- | --- | --- |
| 镜像引用 | ✗ 不显示 | ✗ | ✗ | ✅ **只有这个来源显示** |
| 源归档 | ✅ | ✅ | ✅ | ✗ |
| 基础镜像 | ✅ | ✅ | ✅ | ✗ |
| 运行时版本 | ✅ | ✅ | ✅ | ✅（**必须留空**） |
| 程序入口 | ✅（**必须留空**） | ✅（留空） | ✅（**必填模块名**） | ✗ |
| 参数 | ✅ | ✅ | ✅ | ✗ |
| 独立发布构建 | ✗ | ✅（**不勾**） | ✗ | ✗ |

两处容易困惑的地方：

- **「运行时版本」对四个来源都显示**，但只有归档来源能用。镜像来源填了会被拒
  （`invalid_request`，见 [`fixtures/image/README.md`](./fixtures/image/README.md)）——镜像自带的运行时是你选的那个镜像，没有可覆盖的地方。
- **镜像来源没有「参数」格。** 它下面那组字段（归档 / 基础镜像 / 程序入口 / 参数）整组只在
  「源归档」存在时显示。所以镜像来源是四个来源里唯一**连参数都填不了**的，要验证「参数覆盖镜像 CMD」
  只能走脚本（见文末）。

### 第 4、5、6、7 步的共用机制

**第 4 步（4/7）配置、数据卷与资源上限。** 六个用例里只有 `demo-python-worker` 要动这一页
（填一条 `HEARTBEAT_SECONDS=2`），其余五个**全部留空**。想顺带验证资源上限时可以填：

| 字段 | 格式 | 约束 |
| --- | --- | --- |
| 环境变量 / 密钥 | `NAME=value`，每行一项 | — |
| 数据卷 | `name:/container/path[:ro]`，每行一项 | 只能是 RelaxKonOS 受管卷名 |
| CPU 核心数 | 例如 `1.5` | 大于 0 且不超过 64 |
| 内存（MB） | 例如 `512` | 16 到 65536 之间 |
| 进程数上限 | 例如 `256` | 正整数 |

> 密钥那两栏有个陷阱：**只写密钥名而不填值，会沿用已存储的版本**。首次部署时如果只写名字，
> 会报「某个密钥尚无已存储的版本。」。本批用例不需要密钥。

**第 5 步（5/7）反向代理（可选）。** 六个用例都**留空** → 本次不接入反向代理。填入站点 ID 会在已有站点上新增一条路由，
并在保存前完成校验，且它要求第 3 步有宿主端口（Worker 用例没有宿主端口，也就接不了）。

**第 6 步（6/7）确认。** 核对预览框，然后**勾选「立即发布版本并部署」**，再点底部的「部署」。

> ⚠️ 这个勾选框是**关键**。不勾的话，提交只会创建应用定义，进度页显示「应用已创建。」，
> 不产生任何部署——也不会生成版本、不会起容器。第一次做建议勾上。
>
> 预览框里「入口：」这一行，Java 与镜像用例显示 `—`，Python 用例显示你填的模块名。
> 六者都会在最后一行看到同一句提示：「部署会先停止当前实例，并在新实例健康后完成替换。」

**第 7 步（7/7）部署。** 提交后进入进度页。这是一个**持久化操作**：客户端断线也不会中断它，
重连后仍能看到同一操作的进展（界面顶部横幅也会显示它）。用例之间的差异只有一条：

| 路径 | 阶段序列 |
| --- | --- |
| 三个归档来源（用例 1–5） | 排队中 → 检查前置条件 → 准备源 → **构建镜像** → 创建容器 → 等待就绪 → 切换流量 → 已完成 |
| 现成镜像（用例 6） | 排队中 → 检查前置条件 → **拉取镜像** → 创建容器 → 等待就绪 → 切换流量 → 已完成 |

镜像用例**没有「准备源」和「构建镜像」两步**——没有归档要解压，也没有 Dockerfile 要构建，
它只拉取你给的那个引用，然后解析出它**实际**的镜像身份（image id）。

各阶段的细节：

- **准备源**：解压归档，然后模板在解压结果上挑选入口、生成 Dockerfile 与 `compose.preview.yml`。
  归档与模板不匹配时在这一步失败（例如把裸 `.jar` 当成 Java 来源）。
- **构建镜像 / 拉取镜像**：基础镜像**首次会被拉取**，这一步明显偏慢。归档来源用
  `eclipse-temurin:21-jre`（Java）、`mcr.microsoft.com/dotnet/aspnet:10.0`（.NET Web）、
  `mcr.microsoft.com/dotnet/runtime:10.0`（.NET Worker）、`python:3.12-slim`（Python）。
- **创建容器**：先物化机密文件、创建受管卷，然后建一个**候选**容器（名字带 candidate 后缀）并启动。
  若该应用已有运行中的实例，**旧实例在这一步就被停掉了**（不是等新实例健康后才停），随后在
  「切换流量」里改名、删除。
- **等待就绪**：HTTP 就绪时探的是 `http://127.0.0.1:{宿主端口}{健康路径}`（**固定走回环地址**，
  不用绑定地址那一格），2xx / 3xx 算通过；进程存活就绪时只核对容器处于 running。
  容器已经 exited / dead 时会立刻判失败而不是等满——**其余情况超时 180 秒，每 2 秒探一次**；
  「进程存活」用例走到这一步几乎是瞬间通过。
- **切换流量**：候选容器改名为正式名、旧容器删除、以及（若配了站点）代理路由写入。
  到这里为止新旧实例已经换完，所以**从这一步起不再提供「取消操作」**（该阶段上报 `canCancel: false`）；
  顶部横幅的「取消操作」在「等待就绪」及之前才可用。
- 出现「已完成」即成功，点「关闭」退出向导。

> 因为探测固定在 `127.0.0.1` 上，**把「绑定地址」改成非回环地址会让就绪探测连不上**
> （端口只发布在别的地址上）。六个用例都保持默认的 `127.0.0.1`。

失败时不会停在半路：服务端会尝试恢复，进度页给出的可能是问题码，也可能是一条恢复结论
（「已恢复上一个实例。」／「从失败中恢复同样失败，资源需要人工处理。」／「遗留了需要处理的资源。」）。
常见问题码的中文原文与处置见文末[报错对照表](#报错对照表)。

> **重新部署一个有实例正在运行的应用时会有短暂中断**：旧容器在「创建容器」阶段就停了，而新容器要到
> 「等待就绪」之后才接上。所以别把它当成无中断的滚动替换来用。

---

## 1. demo-java-http（Java JAR）

要上传的归档：`fixtures/java-http/dist/relaxkonos-ad-java-http.zip`

> ⚠️ 同目录下还有一个 `relaxkonos-ad-java-http.jar`，**不要选它**。JavaJar 模板先解压归档，
> 再在解压结果里找 `*.jar`；裸 JAR 传上去会得到「归档与所选模板不匹配。」
> （`archive_content_invalid`）。那个 `.jar` 只用于本地冒烟。

### 第 1 步（1/7）这是什么应用？

| 字段 | 填 |
| --- | --- |
| 名称 | `demo-java-http` |
| 来源 | **Java JAR** |
| 工作负载 | **Web 服务** |

名称填错会直接卡在这一步：「请使用 3–40 个小写字母、数字、连字符或下划线，并以字母或数字开头。」

### 第 2 步（2/7）代码或镜像来自哪里？

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 源归档 | 点「选择本地文件…」→ 选 `relaxkonos-ad-java-http.zip` | 或点「选择服务器文件…」，在**服务端的文件浏览器**里挑一个已在服务器上的归档；那条路只登记路径、不复制文件 |
| 基础镜像 | **留空** | 留空即用模板默认 `eclipse-temurin:21-jre` |
| 运行时版本 | **留空** | 见下 |
| 程序入口 | **必须留空** | ⚠️ 本页唯一会把你打回来的格 |
| 参数 | `--port=8080` | 一行一个参数，所以这就是一个参数 |

底部出现「已暂存 relaxkonos-ad-java-http.zip。」表示上传成功。

**为什么「程序入口」必须留空。** 它旁边的示例文字是「例如 app.py 或 app.jar」，对 Java 来源是
误导性的：那个 `app.jar` 是模板自己定死的落盘路径（`/app/app.jar`），不是让人填的。
`JavaJarTemplate.Validate` 对非空的 `programEntry` 直接抛 `entry_point_invalid`，
界面显示「程序入口对此模板无效。」。注意客户端**不会**提前拦住——客户端只对 Python 项目强制要求
这一项，而这个文本框对所有归档来源都显示，所以你填了也能点「下一步」，直到真正部署才失败。

**为什么「运行时版本」留空。** 它只接受数字和点（`21`、`21.0.5`），一旦填了，服务端会把基础镜像
强制换成 `eclipse-temurin:{版本}-jre`；若同时显式填了基础镜像，那个引用里必须含 `:21`，否则报
「运行时版本与归档不匹配。」。留空则直接落到默认镜像，没有任何附加约束。

**关于「参数」。** 它按**行**切分（不是按空格），每行成为一个独立参数。这些参数会被追加到模板
生成的 `ENTRYPOINT ["java","-XX:MaxRAMPercentage=75","-jar","/app/app.jar"]` 之后。

夹具只认 `--port=8080` 这种**等号**形式：写成 `--port 8080` 两行时它读不到端口，会落回默认 8080。
而本用例的容器端口正好也是 8080，所以**填错了也照样部署成功**——这是这一格最阴的地方，
它不报错，只是你填的参数被静默忽略掉了。想确认参数真的进了容器，把两边一起改成同一个
非 8080 的值（例如容器端口与参数都写 `9000`），部署成功后日志里会出现
`[java-http] listening on 0.0.0.0:9000`；**只改一边**才会在「等待就绪」上超时。

> 上传是暂存：默认 20 分钟过期。在这一页停太久再点部署，可能遇到暂存失效，重选一次文件即可。

### 第 3 步（3/7）如何访问它？

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 容器端口 | `8080` | 默认已是 8080；必须与第 2 步的 `--port=8080` 一致 |
| 宿主端口 | `18081` | 默认是**空的**，必须自己填 |
| 绑定地址 | `127.0.0.1` | 默认值，保持不动 |
| 就绪判定 | **HTTP 探测** | 默认是「进程存活」 |
| 健康检查路径 | `/healthz` | ⚠️ 默认是 `/`，必须改 |

两个必须动手的地方：

- **宿主端口不能留空。** 选 HTTP 探测 + Web 服务时这一格是硬性必须的，否则报
  「HTTP 就绪判定需要宿主端口，因为探测是通过宿主访问容器的。宿主端口为空表示不发布。」
  原因见界面下方那行小字：发布到宿主回环地址是反向代理与健康检查能访问容器的前提。
- **健康检查路径要改成 `/healthz`。** 夹具只在 `/` 与 `/healthz` 上返回 200，其余路径一律 404
  ——这是刻意设计，为了让健康路径填错时**真的失败**，而不是被「什么路径都 200」掩盖。
  保持默认的 `/` 其实也能通过（`/` 返回 200），但那样就验证不到路径是否被真正使用。

### 第 4、5 步

全部留空。

### 第 6 步预览应显示

```
名称：demo-java-http
来源：Java JAR · relaxkonos-ad-java-http.zip
入口：—
端点：127.0.0.1:18081 → 8080
就绪判定：HTTP 探测（/healthz）
环境变量项：0 · 密钥：0
代理站点：无
```

### 验证：四步确认它真的在跑

| # | 怎么看 | 期望 |
| --- | --- | --- |
| 1 | 左侧列表选中 `demo-java-http` | 期望状态「运行」、观测状态「运行中」、版本「1」、端点 `127.0.0.1:18081 → 8080` |
| 2 | 浏览器打开 `http://127.0.0.1:18081/healthz` | 正文 `ok` |
| 3 | 打开 `http://127.0.0.1:18081/` | 一段 JSON，含 `app`（`relaxkonos-ad-java-http`）/ `java` / `host` / `time` |
| 4 | 打开 `http://127.0.0.1:18081/nope` | **404**，正文 `not found: /nope` ——证明健康路径确实是被探测的 |

「日志」标签页里应看到夹具自己打印的启动行：

```
[java-http] listening on 0.0.0.0:8080
[java-http] health path /healthz
[java-http] java.version 21.x.x
[java-http] started at ...
```

`java.version` 应当是 21.x —— 它证明容器用的确实是 `eclipse-temurin:21-jre`，而不是宿主的 JDK 25。

---

## 2. demo-dotnet-web（.NET 发布）

要上传的归档：`fixtures/dotnet-web/dist/relaxkonos-ad-dotnet-web.zip`

### 第 1 步（1/7）这是什么应用？

| 字段 | 填 |
| --- | --- |
| 名称 | `demo-dotnet-web` |
| 来源 | **.NET 发布** |
| 工作负载 | **Web 服务** |

### 第 2 步（2/7）代码或镜像来自哪里？

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 源归档 | 「选择本地文件…」→ `relaxkonos-ad-dotnet-web.zip` | |
| 基础镜像 | **留空** | 留空即用 `mcr.microsoft.com/dotnet/aspnet:10.0` |
| 运行时版本 | **留空** | 填了的话必须与发布包的 `tfm` 推出的版本一致（这里就是 `10.0`），否则「运行时版本与归档不匹配。」 |
| 程序入口 | **留空** | .NET 模板不看这个字段，填了不生效（不像 Java 会被拒） |
| 参数 | **留空** | 需要时会被追加到 `ENTRYPOINT ["dotnet","/app/DotNetWebDemo.dll"]` 之后 |
| 独立发布构建 | **不勾** | ⚠️ 这个包的 `runtimeconfig.json` 里没有 `includedFrameworks`，勾了就报「运行时版本与归档不匹配。」 |

`dotnet-web/` 的发布包是**框架依赖**的（`--self-contained false`），工作负载是 Web
（发布清单里含 `Microsoft.AspNetCore.App`）。服务端会自己读这两点来校验，不听请求里的声明——
所以「工作负载」和「独立发布构建」这两格都不能随便选。

### 第 3 步（3/7）如何访问它？

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 容器端口 | `8080` | 模板会把它写进 `ENV ASPNETCORE_URLS=http://0.0.0.0:8080`，进程跟着它监听；**不要**另外传 `ASPNETCORE_URLS` 配置去改它 |
| 宿主端口 | `18082` | |
| 绑定地址 | `127.0.0.1` | |
| 就绪判定 | **HTTP 探测** | |
| 健康检查路径 | `/healthz` | 默认 `/`，改成 `/healthz` 更能验证路径真的生效 |

### 第 4、5 步

全部留空。**不要**在第 4 步加 `ASPNETCORE_URLS`：模板已经注入，重复注入只会让容器监听的端口和
`containerPort` 脱钩，表现为就绪探测超时。

### 第 6 步预览应显示

```
名称：demo-dotnet-web
来源：.NET 发布 · relaxkonos-ad-dotnet-web.zip
入口：—
端点：127.0.0.1:18082 → 8080
就绪判定：HTTP 探测（/healthz）
环境变量项：0 · 密钥：0
代理站点：无
```

### 验证

| # | 怎么看 | 期望 |
| --- | --- | --- |
| 1 | 列表选中 `demo-dotnet-web` | 观测状态「运行中」、端点 `127.0.0.1:18082 → 8080` |
| 2 | `http://127.0.0.1:18082/healthz` | 正文 `ok` |
| 3 | `http://127.0.0.1:18082/` | JSON，`app` = `relaxkonos-ad-dotnet-web`，`framework` = `.NET 10.0.x`，另含 `machine` / `time` |
| 4 | `http://127.0.0.1:18082/nope` | **404** |

日志里应出现：

```
[dotnet-web] ASPNETCORE_URLS=http://0.0.0.0:8080
Now listening on: http://0.0.0.0:8080
```

第一行是夹具自己打的，它回显的是**模板注入进去的**那个 `ASPNETCORE_URLS` —— 这是「模板确实把容器端口
注入了容器」的直接证据。`framework` 显示 `.NET 10.0.x` 则证明基础镜像是 `aspnet:10.0`
而不是 `runtime:10.0`。

### 这个用例特有的坑

把工作负载选成「后台工作进程」也会失败（`runtime_mismatch`）：这个发布包里含
`Microsoft.AspNetCore.App`，服务端据此判定它是个 Web 应用，和定义里的 Worker 声明矛盾。
两条路只差一格，但结果一定是错。

---

## 3. demo-dotnet-worker（后台工作进程）

要上传的归档：`fixtures/dotnet-worker/dist/relaxkonos-ad-dotnet-worker.zip`

这是 `demo-dotnet-web` 的**反面对照**：同一个模板、同样是框架依赖发布，但项目用的是
`Microsoft.NET.Sdk`（不是 `Sdk.Web`），所以发布清单里**没有** `Microsoft.AspNetCore.App`。

### 第 1 步（1/7）这是什么应用？

| 字段 | 填 |
| --- | --- |
| 名称 | `demo-dotnet-worker` |
| 来源 | **.NET 发布** |
| 工作负载 | **后台工作进程** ← 必须 |

### 第 2 步（2/7）代码或镜像来自哪里？

| 字段 | 填 |
| --- | --- |
| 源归档 | 「选择本地文件…」→ `relaxkonos-ad-dotnet-worker.zip` |
| 基础镜像 | **留空**（留空即 `mcr.microsoft.com/dotnet/runtime:10.0`；Worker 与 Web 在这里选不同镜像） |
| 运行时版本 | **留空** |
| 程序入口 | **留空** |
| 参数 | **留空** |
| 独立发布构建 | **不勾** |

### 第 3 步（3/7）如何访问它？

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 容器端口 | `8080` | 保留默认即可。它不会真的被用到——Worker 没有 HTTP 表面 |
| 宿主端口 | **留空** ← | 服务端只为「Web 工作负载 + 有宿主端口」生成端点对象，所以填了也不显示；但容器**仍会真的把它发布出去**（多占一个端口），留空才对 |
| 绑定地址 | `127.0.0.1` | 保留默认 |
| 就绪判定 | **进程存活** | 把工作负载切成「后台工作进程」时它会**自动**切回这一档并清空健康路径 |
| 健康检查路径 | 无此格 | 只有 HTTP 探测才显示这一格 |

> **别再手动把就绪判定切回 HTTP。** 不填宿主端口时，服务端会在「检查前置条件」这一步就拒绝：
> 「请求被拒绝。」（`invalid_request`）——「HTTP 就绪 + 空宿主端口」是在产生任何副作用之前就被排除的组合。
> 如果切回 HTTP **并且**补了宿主端口，它反而能一路走到最后：容器起得来，但这个控制台程序不监听任何端口，
> 一直探到 180 秒超时，报「就绪探测超时。」。两条路都不通，选「进程存活」才是对的。

### 第 4、5 步

全部留空。

### 第 6 步预览应显示

```
名称：demo-dotnet-worker
来源：.NET 发布 · relaxkonos-ad-dotnet-worker.zip
入口：—
端点：127.0.0.1:— → 8080
就绪判定：进程存活（—）
环境变量项：0 · 密钥：0
代理站点：无
```

端点那行宿主端口是 `—`，这是正常的：Worker 没有可访问的端口。

### 验证（没有 HTTP 可打，只能看状态与日志）

| # | 怎么看 | 期望 |
| --- | --- | --- |
| 1 | 列表选中 `demo-dotnet-worker` | 观测状态「运行中」、版本「1」；**端点一栏为空** |
| 2 | 「日志」→「加载日志」 | 首行 `[dotnet-worker] started at ...`，随后每 **5 秒**一条 `heartbeat=N` |
| 3 | 等 15 秒再加载一次日志 | `heartbeat` 计数在涨（1 → 4 左右）——证明进程真的在跑而不是卡住 |

日志原文：

```
[dotnet-worker] started at 2026-09-20T05:xx:xx.xxxxxxx+00:00
[dotnet-worker] framework .NET 10.0.x
[dotnet-worker] machine <容器 id 前 12 位>
[dotnet-worker] heartbeat=1 at ...
[dotnet-worker] heartbeat=2 at ...
```

心跳间隔是夹具里写死的 5 秒（不受配置影响）。停掉应用时还会看到
`[dotnet-worker] stopping after N heartbeats`。

> 「进程存活」是**明确较弱**的就绪档：它只保证容器在跑。所以这个用例的日志节奏才是唯一的进展证据。

---

## 4. demo-python-web（Python 项目）

要上传的归档：`fixtures/python-web/dist/relaxkonos-ad-python-web.zip`

### 第 1 步（1/7）这是什么应用？

| 字段 | 填 |
| --- | --- |
| 名称 | `demo-python-web` |
| 来源 | **Python 项目** |
| 工作负载 | **Web 服务** |

### 第 2 步（2/7）代码或镜像来自哪里？

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 源归档 | 「选择本地文件…」→ `relaxkonos-ad-python-web.zip` | |
| 基础镜像 | **留空** | 留空即 `python:3.12-slim` |
| 运行时版本 | **留空** | 填了会变成 `python:{版本}-slim`，且必须与归档兼容 |
| 程序入口 | **`app`** ← 必填 | **模块名**，不是文件名 |
| 参数 | **留空** | 会被追加到 `ENTRYPOINT ["python","-m","app"]` 之后 |

Python 来源是四个来源里**唯一把「程序入口」当必填**的：留空或填了非法模块名，客户端直接拦住
（「此模板需要填写入口文件。」）；服务端也会再校验一次（`entry_point_invalid`）。

**要填模块名 `app`，对应归档里的 `app.py`。** 模板用 `python -m <入口>` 启动，所以它要的是
importable 的模块名。

> ⚠️ 填 `app.py` **不会**被拦住——`app` 和 `py` 都是合法模块段，模板只检查第一段 `app` 有没有对应的
> `app.py` 或同名目录，而它正好有。于是校验放行，容器却以 `python -m app.py` 启动，
> 在容器里起不来，最后表现为就绪探测超时。**这是本用例最容易踩的一脚，而且没有任何报错指向填错的那一格。**

### 第 3 步（3/7）如何访问它？

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 容器端口 | `8080` | 与夹具读的 `PORT` 默认值 8080 一致 |
| 宿主端口 | `18083` | |
| 绑定地址 | `127.0.0.1` | |
| 就绪判定 | **HTTP 探测** | |
| 健康检查路径 | `/healthz` | |

> Python 模板**不注入端口变量**，夹具是自己读环境变量 `PORT`（缺省 8080）的。
> 所以只要你把容器端口改成 8080 以外的值，就**必须**在第 4 步补一条非机密配置 `PORT=<值>`，
> 否则进程监听 8080、声明的是别的端口，就绪探测必然超时。本次用默认值，不需要补。

### 第 4、5 步

全部留空。

### 第 6 步预览应显示

```
名称：demo-python-web
来源：Python 项目 · relaxkonos-ad-python-web.zip
入口：app
端点：127.0.0.1:18083 → 8080
就绪判定：HTTP 探测（/healthz）
环境变量项：0 · 密钥：0
代理站点：无
```

### 验证

| # | 怎么看 | 期望 |
| --- | --- | --- |
| 1 | 列表选中 `demo-python-web` | 观测状态「运行中」、端点 `127.0.0.1:18083 → 8080` |
| 2 | `http://127.0.0.1:18083/healthz` | 正文 `ok` |
| 3 | `http://127.0.0.1:18083/` | JSON，`app` = `relaxkonos-ad-python-web`，`python` = `3.12.x`，`flask` = `3.1.0`，另含 `host` / `time` |
| 4 | `http://127.0.0.1:18083/nope` | **404**，正文 `not found` |

日志里应出现：

```
[python-web] listening on 0.0.0.0:8080
[python-web] python 3.12.x
[python-web] health path /healthz
```

`python 3.12.x` 证明容器用的是 `python:3.12-slim` 而不是宿主的 3.13；`flask 3.1.0` 证明
构建期那一次 `pip install --requirement requirements.txt` 真的装上了锁定版本。

### 这个用例特有的坑

**构建镜像这一步需要能访问 PyPI**：`requirements.txt` 里是 `flask==3.1.0`，依赖是**构建期**装进镜像的
（启动时一个包都不装，重启也不会碰包索引）。宿主机没有外网时，这一步会失败并给出
「安装依赖失败。」或「构建镜像失败。」。真要在离线环境跑，改用
[用例 5](#5-demo-python-worker后台工作进程)——它的依赖列表只有注释，构建期完全不联网。

**为什么要求锁定版本：** `requirements.txt` 里每一个非注释行都必须写死版本（`==`，或带
`#sha256=` 摘要的直接构件）。写 `flask>=3.0` 会在构建上下文准备阶段就被拒
（`archive_content_invalid`），根本走不到构建。

---

## 5. demo-python-worker（后台工作进程）

要上传的归档：`fixtures/python-worker/dist/relaxkonos-ad-python-worker.zip`

这是**离线安全**的那个 Python 用例：`requirements.txt` 只有注释，加上只用标准库的 `worker.py`，
镜像构建完全不接触任何包索引。

### 第 1 步（1/7）这是什么应用？

| 字段 | 填 |
| --- | --- |
| 名称 | `demo-python-worker` |
| 来源 | **Python 项目** |
| 工作负载 | **后台工作进程** |

### 第 2 步（2/7）代码或镜像来自哪里？

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 源归档 | 「选择本地文件…」→ `relaxkonos-ad-python-worker.zip` | |
| 基础镜像 | **留空** | 留空即 `python:3.12-slim` |
| 运行时版本 | **留空** | |
| 程序入口 | **`worker`** ← 必填 | 对应归档里的 `worker.py`；同样要模块名，不要写 `worker.py` |
| 参数 | **留空** | |

### 第 3 步（3/7）如何访问它？

| 字段 | 填 |
| --- | --- |
| 容器端口 | `8080`（保留默认，不会被用到） |
| 宿主端口 | **留空** |
| 绑定地址 | `127.0.0.1`（保留默认） |
| 就绪判定 | **进程存活**（切工作负载时会自动切到这一档，健康路径格消失） |

### 第 4 步（4/7）—— 六个用例里唯一要动这一页的

「环境变量」栏填**一项**：

```
HEARTBEAT_SECONDS=2
```

其余（密钥、数据卷、CPU / 内存 / 进程数上限）全部留空。

夹具把心跳间隔读自这个非机密配置项，默认 5 秒。填 2 之后日志节奏会明显变快，
而**归档一个字节都不用改**——所以这一格同时是「配置注入确实生效」的验证手段：
想再看一次效果，把它改成 `10` 重新部署一个版本，日志就会慢下来。

### 第 5 步

留空。

### 第 6 步预览应显示

```
名称：demo-python-worker
来源：Python 项目 · relaxkonos-ad-python-worker.zip
入口：worker
端点：127.0.0.1:— → 8080
就绪判定：进程存活（—）
环境变量项：1 · 密钥：0
代理站点：无
```

「环境变量项：1」就是上面那一条；填错栏位（把配置填进「密钥」）会变成「密钥：1」，那就不对了。

### 验证

| # | 怎么看 | 期望 |
| --- | --- | --- |
| 1 | 列表选中 `demo-python-worker` | 观测状态「运行中」、版本「1」；端点一栏为空 |
| 2 | 「日志」→「加载日志」 | 出现 `[python-worker] heartbeat interval 2.0s (HEARTBEAT_SECONDS=2)` |
| 3 | 连续加载两次日志（间隔几秒） | `heartbeat=N` 的计数按约 2 秒一条的节奏在涨 |

日志原文：

```
[python-worker] started at 2026-09-20T05:xx:xx.xxxxxx+00:00
[python-worker] python 3.12.x
[python-worker] host <容器 id 前 12 位>
[python-worker] requirements are intentionally empty: no package index is contacted
[python-worker] heartbeat interval 2.0s (HEARTBEAT_SECONDS=2)
[python-worker] heartbeat=1 at ...
```

那行 `heartbeat interval 2.0s (HEARTBEAT_SECONDS=2)` 是**环境变量确实进了容器**的直接证据——
括号里回显的就是第 4 步填的值。

停止应用时它会打印 `[python-worker] signal=15 stopping` 与
`[python-worker] stopping after N heartbeats`（收到 SIGTERM 优雅退出）。

### 这个用例特有的坑

- 万一 `HEARTBEAT_SECONDS` 填了非数字（比如 `2s`），进程会在启动时抛错退出，容器随即变成 `exited`。
  部署不会等满 180 秒——容器已退出就无法再变就绪，服务端会立刻判失败，报「就绪探测超时。」。
  填纯数字。
- 与用例 4 不同，这一页**没有**必要填 `PORT`：Worker 不监听端口。

---

## 6. demo-image-nginx（容器镜像）

这个来源**不需要归档文件**：只拉取你给的镜像引用，解析它**实际**的镜像身份（image id），
然后沿用镜像自带的入口。参数与负向用例见 [`fixtures/image/README.md`](./fixtures/image/README.md)。

### 第 1 步（1/7）这是什么应用？

| 字段 | 填 |
| --- | --- |
| 名称 | `demo-image-nginx` |
| 来源 | **容器镜像** |
| 工作负载 | **Web 服务** |

### 第 2 步（2/7）代码或镜像来自哪里？

切到「容器镜像」后，这一页只剩下两格：

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 镜像引用 | **`nginx:1.29.0-alpine`** | 界面上那组「源归档 / 基础镜像 / 程序入口 / 参数」整组消失——这个模板不接受它们 |
| 运行时版本 | **必须留空** | ⚠️ 这一格对镜像来源仍然显示，但填了就是 `invalid_request` |

**镜像引用必须带固定标签。** `nginx:latest` 会被拒（「镜像引用无效。」）。
选带具体版本号的标签是为了让「这个版本部署的到底是哪份镜像」可复现——浮动标签没法作为回滚依据。

> 已知的实现现状：**无标签的引用（例如 `nginx`）当前不会被拒**。校验只挡显式的 `:latest`，
> 而 Docker 会把无标签解析成 `latest`，正好绕开这条规则的本意。所以别依赖它替你兜底，
> 自己写全标签。详见 [`fixtures/image/README.md`](./fixtures/image/README.md)。

### 第 3 步（3/7）如何访问它？

| 字段 | 填 | 说明 |
| --- | --- | --- |
| 容器端口 | **`80`** ← | ⚠️ 默认是 8080，**必须改成 80**。nginx 监听的是 80 |
| 宿主端口 | `18084` | |
| 绑定地址 | `127.0.0.1` | |
| 就绪判定 | **HTTP 探测** | |
| 健康检查路径 | **`/`**（保持默认） | nginx 在 `/` 直接返回 200，不需要改成 `/healthz` |

容器端口这一格最容易漏：留着 8080 的话，健康检查会去打容器的 8080 —— 那里什么都没有，
一直探到 180 秒超时，报「就绪探测超时。」，而容器本身其实是好的。

### 第 4、5 步

全部留空。

### 第 6 步预览应显示

```
名称：demo-image-nginx
来源：容器镜像 · nginx:1.29.0-alpine
入口：—
端点：127.0.0.1:18084 → 80
就绪判定：HTTP 探测（/）
环境变量项：0 · 密钥：0
代理站点：无
```

「来源」后面紧跟的就是镜像引用（其他用例那里显示的是归档文件名）。

### 验证

| # | 怎么看 | 期望 |
| --- | --- | --- |
| 1 | 列表选中 `demo-image-nginx` | 观测状态「运行中」、端点 `127.0.0.1:18084 → 80`；「镜像引用」一栏是 `nginx:1.29.0-alpine`（服务端会记它解析出的 image id，回滚靠这个 id） |
| 2 | `http://127.0.0.1:18084/` | nginx 欢迎页（`Welcome to nginx!`），HTTP 200 |
| 3 | `http://127.0.0.1:18084/nope` | **404**（nginx 自己的页面，不是 nginx 默认之外的响应） |

日志是 nginx 自己的 entrypoint 输出，例如：

```
/docker-entrypoint.sh: Configuration complete; ready for start up
```

**进度页里这条路径最短**：没有「准备源」、没有「构建镜像」，只有 检查前置条件 → 拉取镜像 →
创建容器 → 等待就绪 → 切换流量 → 已完成。它最适合用来单独验证「拉取 → 绑定镜像身份 → 创建 →
就绪 → 激活」这一段。

### 这个用例特有的坑

**向导里填不了参数。** `ImageTemplate` 是允许传 `arguments` 的（会覆盖镜像 CMD），
但那个「参数」输入框只在「源归档」存在时显示，镜像来源看不到它。所以
`fixtures/image/README.md` 里那个 `hashicorp/http-echo:1.0.0` + `-listen=:5678` 的用例
**只能走脚本**（文末的等价命令），向导做不到。

---

## 生命周期操作（六者相同）

先在列表里选中该应用，再点右侧按钮。涉及确认的会弹确认框。

| 操作 | 确认提示原文 | 备注 |
| --- | --- | --- |
| 部署新版本 | 无 | 再走一遍向导（可只改第 2 步的文件），**记得勾「立即发布版本并部署」**，版本号 +1 |
| 停止 | 「停止 {名称}？」 | 切到期望状态「停止」后，观测状态应变为「已停止」 |
| 启动 | 无 | 从「已停止」回到「运行中」 |
| 重启 | 「重启 {名称}？」 | — |
| 回滚 | 「将 {名称} 回滚到版本 {版本}？」 | **需先有已发布的上一版本**；版本是不可变的，回滚是切回已发布的版本，不重新构建 |
| 删除 | 「删除 {名称} 及其容器？受管数据卷会被保留。」 | 只删容器，受管数据卷保留 |

这四条路径的**阶段序列与部署不同**，别拿部署那条去对：

| 操作 | 阶段序列 |
| --- | --- |
| 部署 / 回滚 | 检查前置条件 → 准备源 → （部署才有）构建镜像 → 创建容器 → 等待就绪 → 切换流量 → 已完成 |
| 启动 / 重启 | 检查前置条件 → 切换流量 → 等待就绪 → 已完成 |
| 停止 | 检查前置条件 → 停止上一个实例 → 已完成 |
| 删除 | 检查前置条件 → 停止上一个实例 → 清理中 → 已完成 |

「操作」标签页里能看到每次操作的持久记录与状态（排队中 / 执行中 / 成功 / 失败 / 已取消 / 已中断）。
**「取消操作」按钮在「等待就绪」及更早的阶段可用**；到了「切换流量」就不再提供，硬试会得到
「该操作已超过可取消的阶段。」。

> 想验证**幂等**（同一个 `Idempotency-Key` 重放不会产生第二个版本），向导做不到——它每次提交都
> 自己生成新键。用 `scripts/run-deployment-flow.ps1 -TestIdempotency` 验证。

---

## 失败时先看「失败步骤的输出」

**构建或拉取失败时，界面会直接给出这一步的原始输出** —— 这是最省事的排查入口，比对着问题码猜要快得多。

两处都能看：

| 位置 | 怎么出现 |
| --- | --- |
| 向导第 7 步 | 操作失败后**自动加载**，在错误横幅下方以等宽字体显示 |
| 工作区「操作」标签页 | 每个**已结束**的操作行有「查看日志」按钮，点开显示在同一行下方 |

它记录的是**产出镜像那一步**的命令输出：归档来源是 `docker build` 的输出，镜像来源是 `docker pull`
的输出。只有失败的操作会记录，成功的操作不占地方。

典型的三类内容：

```
#2 [internal] load metadata for docker.io/library/eclipse-temurin:21-jre
#2 ERROR: failed to fetch oauth token: Post "https://auth.docker.io/token": Bad Gateway
```
→ 宿主连不上镜像仓库。见上文[「基础镜像必须拉得下来」](#基础镜像必须拉得下来)。

```
#5 [2/3] RUN useradd --system --uid 10001 --create-home --shell /usr/sbin/nologin appuser
#5 ERROR: process "/bin/sh -c useradd ..." did not complete successfully: exit code: 127
```
→ 基础镜像里没有 `useradd`（Alpine 系）。换回默认镜像，或换一个完整发行版。

```
#8 [3/3] RUN pip install --no-cache-dir --requirement requirements.txt
#8 ERROR: Could not find a version that satisfies the requirement flask==3.1.0
```
→ 构建期到不了 PyPI。

三点说明：

- 输出是**行尾截断**的：最多保留最后 120 行，每行最多 512 字符，界面若显示「输出已截断」就说明
  开头被丢掉了。
- 客户端**不会**替你解释这些行：它就是命令原话。含凭据的字符串在服务端持久化前已被同一个
  日志净化器处理，不会写进账本。
- 这两处读的是**持久记录**，不是内存里的日志。所以关掉向导、重启客户端之后仍然看得到。

---

## 报错对照表

| 界面文案 / 问题码 | 原因 | 怎么改 |
| --- | --- | --- |
| 程序入口对此模板无效。（`entry_point_invalid`） | 第 2 步「程序入口」填了东西（Java 来源，或镜像来源） | 清空它。.NET 用例填了不会被拒，但也不生效 |
| 此模板需要填写入口文件。（`entry_required`） | Python 项目没填程序入口 | 第 2 步填模块名 `app` / `worker` |
| 归档与所选模板不匹配。（`archive_content_invalid`） | 选了裸 `.jar`；或 `requirements.txt` 没锁版本；或归档里没有符合条件的 `*.runtimeconfig.json` / `*.jar` | 改选 `*.zip`；确认第 1 步来源选对了；确认依赖锁死 |
| 请求被拒绝。（`invalid_request`） | 给归档来源传了 `imageReference`；给镜像来源传了基础镜像 / 入口 / 运行时版本；勾了不支持的「独立发布构建」；或「HTTP 就绪 + 宿主端口为空」——最后这条在「检查前置条件」就被挡下 | 按第 2 步的可见性表清掉多余字段；HTTP 就绪时补上宿主端口 |
| 运行时版本与归档不匹配。（`runtime_mismatch`） | 填了运行时版本但基础镜像里不含 `:{版本}`；或 .NET 的「独立发布构建」勾选与实际发布不符；或把 .NET Web 包按 Worker 部署（反之亦然） | 两者都留空 / 不勾；工作负载按发布包实际情况选 |
| 镜像引用无效。（`image_reference_invalid`） | 镜像引用带了 `:latest`，或含非法字符 | 用 `nginx:1.29.0-alpine` 这类固定标签 |
| HTTP 就绪判定需要宿主端口（`host_port_required_for_http`） | 第 3 步宿主端口为空 | 填 18081–18084 之一；Worker 用例改用「进程存活」 |
| 健康检查路径必须以 “/” 开头。 | 路径没写前导斜杠 | 填 `/healthz` 或 `/` |
| 就绪探测失败 / 超时（`health_check_failed` / `health_check_timeout`） | 健康路径填错；`--port=` / `PORT` / 容器端口三者不一致；Python 入口填了 `app.py`；镜像用例容器端口没改成 80 | 见各用例「特有的坑」；超时是 180 秒 |
| 宿主端口已被占用 / 与另一个已部署应用冲突（`port_unavailable` / `port_conflict`） | 18081–18084 被别的进程或另一个应用占了 | 换一个端口，并同步调整验证用的 URL |
| 找不到该镜像 / 镜像仓库拒绝了凭据（`image_not_found` / `registry_authentication_failed`） | 镜像引用打错，或私有仓库缺凭据 | 用 `nginx:1.29.0-alpine`；私有仓库先配好凭据 |
| 该镜像不支持此服务器的平台（`platform_unsupported`） | 镜像没有 `linux/amd64`、`linux/arm64` 或 `linux/arm` 变体 | 换一个多架构或 Linux 变体的镜像 |
| 构建镜像失败。且失败几乎是**瞬间**出现的（`build_failed`） | 服务端**在调用 `docker build` 之前**就拒绝了：Docker 引擎的构建路径白名单（`DockerCliEngineOptions.BuildRoots`）里没有部署的构建根目录。特征：进度从 `Building` 到 `Failed` 只隔几毫秒，服务端日志里**没有 `docker build` 命令行** | 确认服务端已包含「把部署构建根并入构建白名单」的修复；没有的话需在配置节 `DockerEngine:BuildRoots` 里显式写**绝对路径**，且与 `ApplicationDeployments:RootDirectory` 手工对齐 |
| 构建镜像失败。且日志里能看到 `docker build`（`build_failed`） | 构建真的跑了但失败：拉不到基础镜像（`Bad Gateway` / 超时），或 Python 装不上依赖 | 见上文「基础镜像必须拉得下来」：配 registry mirror，或把第 2 步「基础镜像」改成可达镜像源上的同一镜像并留空「运行时版本」 |
| 安装依赖失败。（`dependency_install_failed`） | 构建期 `pip install` 装不上（离线环境） | 先让宿主能访问 PyPI；不需要依赖的用例改用 `demo-python-worker` |
| 容器引擎不可用 / 未安装容器引擎 | Docker 没起或不可达 | 启动 Docker Engine |
| 另一个操作正在处理此应用。（`resource_conflict`） | 同一个应用上重复发了变更请求 | 等当前操作跑完再发 |
| 服务器未提供“应用部署”接口。（`http_404`） | User Mode 宿主，或客户端与服务端版本不匹配 | 换系统模式宿主；或对齐版本 |
| 引用的服务器文件已无法读取。（`file_reference_unavailable`） | 用了「选择服务器文件…」但文件被移动 / 删除 | 改用「选择本地文件…」重新上传 |
| 某个密钥尚无已存储的版本。（`secret_version_missing`） | 第 4 步密钥只写了名字没写值 | 补上 `NAME=value`，或清空该栏 |
| 该操作已超过可取消的阶段。（`not_cancellable`） | 点「取消操作」太晚 | 等它跑完，失败后按问题码处理 |
| 部署账本不可用。（`store_unavailable`） | 三个账本文件缺了任意一个 | 整目录删 `RelaxKonOS.Server/data/application-deployments/` 再重试 |

---

## 等价的一条命令

不想点七步向导时，`scripts/run-deployment-flow.ps1` 把整条链路按协议打一遍，并留下
`stage` / `state` / `progress` 证据：

```powershell
.\scripts\run-deployment-flow.ps1 -ServerUrl http://127.0.0.1:5000 `
    -Identifier <账号> -Password <密码> -Case java-http `
    -TestIdempotency -Rollback -Lifecycle -Delete
```

`-Case` 可换成六个用例中任意一个（`java-http`、`dotnet-web`、`dotnet-worker`、`python-web`、
`python-worker`、`image-nginx`）。两者的字段语义完全一致（同一个 `DeploymentSourceInputDto`），
所以向导里填错的值，脚本里同样会错。

脚本能做而向导做不到的两件事：**验证幂等重放**，以及**给镜像来源传参数**
（向导对镜像来源不显示「参数」格，见用例 6 的「这个用例特有的坑」）。
