# 自建云中继部署指南

本指南部署 Codex Phone Reminder 的云中继。它用于解决校园网、移动网络或异地网络中电脑与 Android 设备不能直接建立 TCP 连接的问题。

它**不需要**为电脑或平板配置固定的校园网 IP，也不需要把 Windows Agent 的 `5187`、`5188` 端口暴露到公网。电脑 Agent 和 Android 客户端都主动连接到一个固定的 HTTPS 域名；即使 DHCP 地址、SSID 或所在地变化，也能重新连接。

> 本文假设代码仓库中包含 `src/CodexPhoneReminder.Relay`。以下示例中的域名、邮箱、IP、密钥均为占位符，必须替换为自己的值。

## 1. 架构与安全边界

```text
Windows Agent ────── HTTPS / 长连接 ──────┐
                                         │
Android App ───────── HTTPS / 长连接 ────┼── relay.example.com:443
                                         │          │
                                         │       Caddy
                                         │          │（Docker 内网）
                                         └── Cloud Relay :8080
```

- VPS 只向公网开放 `80/tcp` 与 `443/tcp`；中继应用的 `8080` 仅存在于 Docker 内网。
- Caddy 申请并续期公开受信任的 TLS 证书，并将 HTTPS 长轮询请求转发给中继。
- Agent 使用 `Relay:Url`、`Relay:BootstrapKey` 注册到中继。Android 不应保存或显示这个 bootstrap key；手机通过现有的 Agent 配对流程得到自己的设备授权。
- TLS 防止传输过程被窃听或篡改；当前 Agent 与 Android 版本还会使用每次配对生成的 AES-256-GCM 密钥加密业务请求和响应。Relay 只保存/转发密文，仍会看到 Agent/设备标识、连接时间、IP、密文大小和 HTTP 状态等路由元数据；不要把中继的磁盘、日志或 bootstrap key 交给不可信的人。

## 2. 部署前准备

需要一台可从公网访问的 Linux VPS、一个域名，以及该域名的 DNS 控制权。服务器规格对个人使用很低：1 vCPU、1 GB 内存通常足够；可靠性和网络质量比算力更重要。

在开始前完成以下事项：

1. 为中继单独建立子域名，例如 `relay.example.com`。
2. 创建指向 VPS 公网 IPv4 的 `A` 记录；只有 VPS 已正确配置 IPv6 时才创建 `AAAA` 记录。错误的 `AAAA` 记录会导致部分网络拿不到证书或连接失败。
3. 确认 DNS 已生效：

   ```bash
   dig +short relay.example.com A
   ```

4. 在 VPS 安装 Docker Engine 与 Docker Compose v2，并确保当前账户可运行 `docker compose`。
5. 在云厂商安全组、防火墙和 VPS 本机防火墙中允许 `80/tcp` 与 `443/tcp`。保留已有 SSH 规则，**不要**为了此服务开放 `5187`、`5188` 或 `8080`。

使用 Caddy 的 HTTP-01 证书校验时，DNS 解析出的 VPS 必须能从公网收到 80 和 443 端口的连接。若域名使用 CDN/代理服务，首次部署建议先设为纯 DNS；确认该服务支持较长的 HTTPS 请求与完整 TLS 后再启用代理。

## 3. 准备部署目录和秘密

把本项目的代码放到 VPS 上，例如：

```bash
sudo mkdir -p /opt/codex-phone-reminder
sudo chown "$USER":"$USER" /opt/codex-phone-reminder
cd /opt/codex-phone-reminder

# 使用你自己的 Git 仓库，或以 rsync / SFTP 上传本项目完整目录。
git clone <你的仓库地址> .
```

在仓库根目录创建 `.env`。该文件仅保存在 VPS，本身不应提交到 Git：

```dotenv
RELAY_DOMAIN=relay.example.com
CADDY_EMAIL=you@example.com
RELAY_BOOTSTRAP_KEY=替换为下方生成的随机值
```

生成密钥时不要复用配对码、WireGuard 私钥或账户密码：

```bash
openssl rand -hex 32
```

将输出填入 `RELAY_BOOTSTRAP_KEY`，再限制文件权限：

```bash
chmod 600 .env
```

这个 key 是 Agent 注册到中继的服务器端凭据。泄露后应立即替换 VPS `.env` 和每台 Agent 的 `Relay__BootstrapKey`，然后重启所有相关服务。

## 4. 创建 Docker Compose 与 Caddy 配置

在仓库根目录创建 `docker-compose.yml`：

```yaml
name: codex-phone-reminder

services:
  relay:
    build:
      context: .
      dockerfile: src/CodexPhoneReminder.Relay/Dockerfile
    environment:
      ASPNETCORE_URLS: http://+:8080
      Relay__BootstrapKey: ${RELAY_BOOTSTRAP_KEY:?RELAY_BOOTSTRAP_KEY is required}
      Relay__StatePath: /app/data/relay-state.json
      Relay__RequestLifetimeMinutes: 10
      Relay__ResultRetentionMinutes: 10
      Relay__MaxPendingRequestsPerDevice: 32
    volumes:
      - relay-data:/app/data
    expose:
      - "8080"
    restart: unless-stopped

  caddy:
    image: caddy:2-alpine
    depends_on:
      - relay
    environment:
      RELAY_DOMAIN: ${RELAY_DOMAIN:?RELAY_DOMAIN is required}
      CADDY_EMAIL: ${CADDY_EMAIL:?CADDY_EMAIL is required}
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
      - caddy-config:/config
    restart: unless-stopped

volumes:
  relay-data:
  caddy-data:
  caddy-config:
```

然后创建 `Caddyfile`：

```caddyfile
{
    email {$CADDY_EMAIL}
}

{$RELAY_DOMAIN} {
    encode zstd gzip

    # flush_interval 可减少长轮询响应的额外缓冲延迟。
    reverse_proxy relay:8080 {
        flush_interval -1
    }

    log {
        output stdout
        format json
    }
}
```

`relay` 没有 `ports:`，只有 `expose:`；这是有意设计。不要把 `8080` 映射为 VPS 公网端口，也不要绕过 Caddy 直接访问它。

中继只暂存待处理请求和已加密响应，默认请求有效期、响应保留期均为 10 分钟；每台设备同时最多 32 个未完成请求。Android 在打开对话时可能每两秒刷新一次，因而不要把 `Relay__ResultRetentionMinutes` 随意设为数小时或数天；手机在短暂断线后仍有充足时间重试，而较短的保留期与待处理队列上限可避免状态卷无限增长。

生产环境建议将 `caddy:2-alpine` 固定到经过测试的版本或镜像摘要；升级前先阅读 release note，并在低风险时间窗口操作。

## 5. 配置 VPS 防火墙

云厂商安全组和 VPS 本机防火墙是两层独立控制，二者都需要允许 HTTPS。以 UFW 为例，先确保 SSH 已允许，再增加 Web 端口：

```bash
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw status verbose
```

如果 UFW 尚未启用，确认不会切断现有 SSH 会话后才执行：

```bash
sudo ufw enable
```

不要添加以下规则：

```text
5187/tcp、5188/tcp、8080/tcp、任意数据库端口、Docker daemon 2375/tcp
```

它们都不应成为公网服务。

## 6. 启动并验证中继

在仓库根目录运行：

```bash
docker compose config
docker compose up -d --build
docker compose ps
docker compose logs --tail=100 relay caddy
```

`docker compose config` 能先发现缺失变量或 YAML 错误。首次启动时，Caddy 日志应显示证书申请成功；若失败，优先检查 DNS、80/443 的云安全组和本机防火墙。

从 VPS 或另一台公网网络设备验证：

```bash
curl --fail --silent --show-error https://relay.example.com/health
```

当前 Relay 的健康检查应返回成功状态（通常是 JSON）。也可确认 TLS 证书的域名：

```bash
openssl s_client -connect relay.example.com:443 -servername relay.example.com </dev/null 2>/dev/null \
  | openssl x509 -noout -subject -issuer -dates
```

不要用伪造的注册请求测试业务接口；这些请求会产生无意义的认证错误。中继可用性的最终验证应来自一个真实 Agent 和已配对 Android 设备。

## 7. 配置 Windows Agent

在运行 Agent 的电脑上，为 Agent 设置下列配置。推荐用环境变量，避免把 VPS 密钥写进会提交的 `appsettings.json`：

```powershell
$env:Relay__Url = 'https://relay.example.com'
$env:Relay__BootstrapKey = '<与 VPS .env 完全相同的随机 key>'
$env:Relay__AgentId = ''              # 留空时由 Agent 生成并持久化
$env:Relay__PollWaitSeconds = '20'    # 默认值；一般无需改动

dotnet run --project E:\codexphonereminder\src\CodexPhoneReminder.Agent
```

如果 Agent 通过计划任务、服务包装器或桌面快捷方式启动，请把相同的 `Relay__Url` 与 `Relay__BootstrapKey` 放在该启动方式的受保护环境配置中，并重启 Agent。`AgentId` 生成后应保持不变；不要在每次启动时手动生成新的值。

对应的配置节形式如下，便于需要使用配置文件的部署方式参考：

```json
{
  "Relay": {
    "Url": "https://relay.example.com",
    "BootstrapKey": "仅保存在本机和 VPS 的秘密",
    "AgentId": "",
    "PollWaitSeconds": 20
  }
}
```

请勿将 bootstrap key 写进 Android APK、网页 JavaScript、配对二维码、截图或 Git 仓库。Android 设备应继续使用现有的配对、设备令牌和撤销机制；它不需要知道中继的注册秘密。

Agent 启动后检查其日志或健康状态中的中继连接状态。此时电脑只需能出站访问 `https://relay.example.com:443`；它不需要固定公网 IP、入站端口映射或 WireGuard peer endpoint。

## 8. Android 配对与回退验证

1. 先保持电脑 Agent 运行，并确认其已显示中继连接成功。
2. 用电脑本地配对页或 Android 的重新配对入口完成一次新的本地配对。此时 Agent 必须已连接中继；配对响应会自动将中继凭据安全保存到 Android Keystore，应用没有额外的“选择云中继”开关。
3. 关闭同一局域网直连的可能性进行验证，例如让手机切换到移动数据、不同 Wi-Fi，或临时断开 WireGuard。
4. 在 Android 中打开已有任务、发送一条低风险测试消息，并观察任务回复、实时进度和审批通知。
5. 恢复同一局域网后再次测试；正常行为应是本地可达时优先直连，不可达时自动使用云中继。

WireGuard 可以保留为校园网备用方案，但云中继已经不依赖它。尤其不要继续把会变化的校园 DHCP 地址写入 WireGuard 的 `Endpoint`。

## 9. 日常运维、备份与升级

查看状态与日志：

```bash
cd /opt/codex-phone-reminder
docker compose ps
docker compose logs --tail=200 relay
docker compose logs --tail=200 caddy
```

更新代码后：

```bash
git pull
docker compose up -d --build
```

`relay-data` 是持久卷，其中可能包含 Agent 注册、投递状态或其他中继运行状态。升级、迁移 VPS 或清理 Docker 前先备份它，并将权限为 `600` 的 `.env` 一起放进同一个受保护备份位置：

```bash
docker run --rm \
  -v codex-phone-reminder_relay-data:/data:ro \
  -v "$PWD":/backup \
  alpine tar czf /backup/relay-data-backup-$(date +%F).tar.gz -C /data .

# `.env` 是恢复中继所必需的秘密；复制到加密或权限受限的备份位置。
install -m 600 .env /安全备份目录/relay.env
```

备份文件及 `.env` 都含敏感信息或可用于恢复敏感状态，应加密保存，并避免上传到公开网盘或提交 Git。不要使用 `docker compose down -v`，除非明确希望永久删除中继状态和证书缓存。

## 10. 常见故障排查

| 现象 | 优先检查 |
| --- | --- |
| Caddy 无法签发证书 | A/AAAA 是否指向该 VPS；云安全组和 UFW 是否允许 80/443；域名是否被不兼容的 CDN 代理。 |
| 域名能打开但 Agent 无法连接 | Agent 的 `Relay__Url` 是否为 `https://`；电脑代理/防火墙是否允许出站 443；服务器 `docker compose logs relay` 是否出现认证失败。 |
| 日志提示 bootstrap key 无效 | VPS `.env` 与 Agent `Relay__BootstrapKey` 必须逐字符相同；修改 `.env` 后需要 `docker compose up -d` 重建 relay。 |
| Android 仅本地可用 | 确认 Agent 已连上中继，再重新打开 Android 应用；检查客户端是否已获取云回退配置，而不是沿用旧的局域网配对资料。 |
| 连接偶尔掉线 | 保持 `Relay__PollWaitSeconds=20`；检查 VPS 内存、磁盘和 Caddy 日志，避免由 CDN/反向代理设置过短的空闲连接超时。 |
| VPS 重启后不可用 | 确认 Docker 服务随系统启动、两个 Compose 服务均为 `restart: unless-stopped`，并检查持久卷仍存在。 |

在提交问题日志前，删除 bootstrap key、设备 token、配对码、完整请求体、IP 与域名中不希望公开的部分。认证失败通常已足够定位配置问题，不需要分享秘密。
