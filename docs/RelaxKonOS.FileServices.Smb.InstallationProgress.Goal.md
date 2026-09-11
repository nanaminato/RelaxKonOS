# RelaxKonOS SMB 安装进度与后台任务 Goal

> 状态：待实施  
> 建立日期：2026-09-11  
> 适用范围：`.NET 10` Server、Avalonia Client、Linux Samba 与 Windows Server SMB 安装

本 Goal 将现有同步 SMB 安装改为可查询的后台任务。目标是让客户端显示可信的安装状态：Linux 显示软件源更新、真实下载百分比、安装/配置阶段与最终结果；Windows 显示 File Server role 的真实安装阶段。不得以计时器或估算值伪造百分比。

## 1. 成功标准

- `POST /api/v1.0/file-services/smb/install` 创建任务并立即返回 `202 Accepted` 与任务 DTO；不再保持 HTTP 请求直到包安装结束。
- `GET /api/v1.0/file-services/smb/installations/{operationId}` 返回任务状态，直到成功、失败或中断后的恢复结论。
- Linux `apt-get` 使用固定的 `APT::Status-Fd` 状态流；仅将已验证的阶段和 `0..100` 百分比映射为 DTO。包名、URL、命令输出、stderr、凭据和仓库细节不得进入 HTTP、日志或 UI。
- 无法给出可信百分比时，DTO 的 `progress` 为 `null`，客户端显示明确阶段（例如“正在配置 Samba”），不得显示猜测数值。
- Client 在任务进行时覆盖“请先安装 SMB”，显示确定型进度条或不确定型阶段指示；重连后凭 operation ID 继续查询。
- 成功后刷新 SMB 状态；失败时显示稳定 problem code；重启/中断后的任务不会永久显示“进行中”。

## 2. 当前接口（直接替换）

项目未正式发布，因此不保留同步安装响应、旧路由、旧 DTO 或双格式解析。

```text
POST /api/v1.0/file-services/smb/install
  -> 202 FileServiceInstallationDto

GET /api/v1.0/file-services/smb/installations/{operationId}
  -> 200 FileServiceInstallationDto
```

`FileServiceInstallationDto` 至少包含：

```text
operationId, state, stage, progress?, problemCode?,
createdAt, startedAt?, completedAt?
```

状态枚举：`Queued | Running | Succeeded | Failed | Interrupted`。

阶段枚举：`Queued | Preparing | UpdatingPackageLists | Downloading | Installing | Configuring | Verifying | Completed | Failed | Interrupted`。

`progress` 只表示当前可度量阶段的百分比，范围固定为 `0..100`；它不是跨阶段的虚构总进度。`Downloading` 的百分比必须来自 apt 状态；`Installing`/`Configuring` 仅在包管理器给出相应状态百分比时携带数值。

## 3. 架构

```text
Client ViewModel
  ├─ POST install -> operationId
  └─ poll GET installation/{operationId} (500 ms–2 s, 退避)
       ↓
FileServiceInstallationStore (持久化、单 SMB 安装互斥、恢复)
       ↓ progress callback
FileServiceManager / LinuxSambaPlatformAdapter
       ↓
LocalPrivilegedOperationRunner (只解析结构化进度帧)
       ↓
root-owned Helper -> fixed apt-get + APT::Status-Fd
```

任务存储是宿主全局、持久化的非秘密元数据。它记录 actor 的安全引用、operation ID、状态、阶段、百分比、时间和 problem code；不记录 apt 原始行、路径、命令、软件源 URL、密码、token 或 stderr。启动时，原本为 `Queued`/`Running` 的任务必须标记为 `Interrupted`，并提供稳定的 `file-services.smb.installation_interrupted` problem code。

同一时刻只允许一个 SMB 安装任务。新的安装请求在已有活动任务时返回该活动任务或稳定冲突结果；不得并发运行 apt/dpkg。

## 4. Helper 进度协议

Linux one-shot Helper 的 stdout 仍为封闭的结构化协议，禁止混入 apt 输出。协议版本升级并直接替换旧版本。

```json
{"kind":"progress","stage":"Downloading","progress":42}
{"kind":"progress","stage":"Configuring","progress":null}
{"kind":"result","success":true,"exitCode":0,"problemCode":"None"}
```

- 每帧为一行 JSON（NDJSON），最大帧大小与总帧数必须受限。
- Server 只接受 `kind`、冻结阶段枚举、整数百分比和固定结果字段；未知帧、越界百分比或非 JSON 输出使操作失败为 Helper protocol error。
- Helper 固定运行 `apt-get`，以受控 status fd 接收状态并内部排空 stdout/stderr；不得把任意 command、参数、包名、环境或仓库输入暴露给 Client/Server。
- Windows named-pipe Helper 继续使用认证的单结果帧；Windows role API 若提供阶段信息则映射为同一 DTO，否则仅报告无百分比阶段。

## 5. 授权、审计与失败

- 创建任务要求 `FileServicesManage` 与当前 JWT、目标 `smb:managed` 的 `HostElevationCapability.SmbManage`；查询要求任务创建者或具备相同管理权限的管理员，不能泄露其他用户的任务状态。
- 创建、开始、完成和失败均写入既有文件服务审计，资源固定为 `smb:managed`；进度轮询不产生审计噪音。
- Helper 缺失、平台不支持、apt 失败、超时、协议损坏、Server 重启与客户端断开均产生稳定 problem code。客户端断开不得取消已提交的 root 操作。
- 任务完成后必须重新执行 Samba 检测；只有检测到实际安装/服务状态才标记成功。

## 6. Client 行为

- 点击“安装”后立即禁用重复安装并显示 `Preparing`，不再显示“请先安装 SMB”。
- `Downloading` 显示“正在下载 Samba：{progress}%”；`Installing`、`Configuring` 和 `Verifying` 显示本地化阶段文本与对应确定/不确定进度控件。
- 窗口重开或网络短暂断开时保留 operation ID 并恢复查询；查询失败只显示连接错误，不得把任务改写为失败。
- 终态后停止轮询并刷新 status/capabilities/shares/users。

## 7. 实施顺序与验收

1. 在 `Shared/RelaxKonOS.Protocol/FileServices` 增加任务 DTO、枚举、problem code 和当前路由；更新所有 Server、Client、测试与文档调用方。
2. 实现持久 `FileServiceInstallationStore`、单任务锁、授权查询 Endpoint 和恢复策略。
3. 将 Linux Helper 的 Samba 安装改为受控 apt status 解析与 NDJSON 进度帧；更新 Local transport 的帧验证。确认 stdout 永不混入包管理器文本。
4. 让 Manager、Provider、Adapter 与 SMB privileged abstraction 传递强类型进度；Windows 映射可用阶段。
5. 实现 Client 启动/轮询、阶段本地化、确定型进度条和重连恢复。
6. 添加测试：apt status 行映射、非法帧拒绝、单任务互斥、重启恢复、权限隔离、断开后继续、成功刷新、失败呈现、Client 轮询/停止与 Windows 无百分比阶段。
7. 在隔离 Ubuntu VM 验收：缓存命中与未命中下载、慢网络、apt 失败、超时、Server 重启、Helper 更新、Samba 启动失败与 UI 重连。

完成条件：所有自动化测试通过，隔离 VM 能观察真实下载百分比和安装/配置阶段，且不存在伪造百分比、同步长请求、协议输出污染、跨用户任务泄露或未授权查询。
