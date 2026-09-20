# 现成镜像用例（ApplicationSourceKind = Image）

这个来源不需要归档文件：`ImageTemplate` 只拉取你给出的镜像引用，解析它**实际**的镜像身份
（image id），然后沿用镜像自带的入口。因此本目录没有制品，只有下面这组推荐参数。

## 校验要点

`ImageTemplate.Validate` 会拒绝多余字段：`imageReference` 是唯一允许的输入，
`baseImage` / `runtimeVersion` / `programEntry` 三者**任一非空即 `invalid_request`**
（见 `ApplicationTemplates.cs` 的 `ImageTemplate.Validate`）。`selfContained` 同样不被支持。
`arguments` 允许传，会作为容器命令覆盖镜像的 CMD。

镜像引用必须是**固定标签**：`IsPinnedImageReference` 会拒绝 `:latest` 和无标签引用，
返回 `application-deployment.image_reference_invalid`。

## 推荐用例

### 正向：nginx 静态站点

| 字段 | 值 |
| --- | --- |
| name | `demo-image-nginx` |
| sourceKind | `Image` |
| workloadKind | `Web` |
| readinessLevel | `Http` |
| healthCheckPath | `/` |
| containerPort | `80` |
| hostPort | `18084` |
| bindAddress | `127.0.0.1` |
| imageReference | `nginx:1.29.0-alpine` |
| arguments | 不传 |

nginx 默认在 80 端口返回 200，所以 `/` 就绪检查能直接通过，适合验证「拉取 → 固定镜像身份 →
创建 → 就绪 → 激活」这条最短路径。

### 正向：带参数的镜像

`arguments` 覆盖镜像 CMD 是 `Image` 来源唯一可传的运行期输入，可以用它验证参数确实进入了容器
命令：

| 字段 | 值 |
| --- | --- |
| imageReference | `hashicorp/http-echo:1.0.0` |
| containerPort | `5678` |
| arguments | `-listen=:5678`、`-text=hello from relaxkonos` |

### 负向用例

| 场景 | imageReference / 其他字段 | 预期问题码 |
| --- | --- | --- |
| 浮动标签 | `nginx:latest` | `application-deployment.image_reference_invalid` |
| 传了不属于本模板的字段 | 同时传 `baseImage: eclipse-temurin:21-jre` | `application-deployment.invalid_request` |
| 传了入口字段 | 同时传 `programEntry: /bin/sh` | `application-deployment.invalid_request` |
| 镜像不存在 | `nginx:0.0.0-does-not-exist` | `application-deployment.image_not_found` |
| 架构不匹配 | 一个仅发布 Windows 容器的引用 | `application-deployment.image_platform_mismatch` |

> `latest` 被拒是刻意的：发布版本必须绑定可复现的镜像身份，浮动标签无法作为回滚依据。

### 一处实现观察（建议复核，不是本用例的断言）

`ApplicationDeploymentValidation.IsPinnedImageReference` 的实现是：

```csharp
=> value is not null && !value.EndsWith(":latest", ...) && !value.Contains(":latest@", ...);
```

它只拒绝**显式**的 `:latest` 与 `:latest@`。因此**无标签引用（例如 `nginx`）会通过固定性校验**，
而 Docker 会把无标签引用解析为 `latest`——这正好绕开了本条规则想守住的可复现性。上面的负向用例
没有把「无标签」列为预期被拒，是因为按当前实现它确实不会被拒。若认为这是缺口，需要的是修改
校验实现，而不是修改本用例。

> 注：`image_reference_invalid` 中「引用合法」的部分由 `IsValidImageReference` 判定，它只允许
> 字母、数字与 `/ : . _ -`，长度 1–255。

