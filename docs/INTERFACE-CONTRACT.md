# 网站对接契约与可选提案

以下契约供桌面维护使用。网站 v10.3 由网站维护上下文独立发布；桌面维护继续遵守网站只读边界。
下方提案保留历史，其中 A 已实现最小目录上下文，D 已在网站补齐受管理的默认分组报价。

## 已接入现有接口

| 接口 | 凭据和行为 |
|---|---|
| GET /、POST /__entry | HTML csrf 与独立入口密码；CookieContainer 处理 host cookie；不把入口当账号身份 |
| GET /api/status | 系统状态、密码加密能力、quota_per_unit；入口拦截给明确错误 |
| GET /api/user/login/encryption-key | enabled/kid/public_key；RSA-OAEP/SHA256 与 v2 AES-GCM 信封 |
| POST /api/user/login | username 与密码或加密信封；接收 access_token/access_expires_at/user 或 require_verification/flow_token/methods |
| POST /api/user/login/verify | flow_token、method=2fa、code；没有冒充 Passkey |
| POST /api/user/auth/refresh、logout | 正常 refresh cookie，固定站点 Origin；DPAPI 持久保存会话 |
| GET /api/user/self | 账号 Bearer；余额按 quota_per_unit 转换成人民币 |
| GET /api/token/ | 本人列表，分页，group/status/remain_quota/unlimited_quota/expired_time |
| POST /api/token/:id/key | 本人完整模型 Key，直接存 DPAPI，不放日志和剪贴板 |
| POST /api/token/ | 有限 quota、30 天期限、明确分组、无跨组重试；不会充值 |
| DELETE /api/token/:id | 明确用户操作后删除本人 Key；移除本机对应凭据 |
| GET /api/pricing | group_pricing 是最终 CNY；只用 enable_groups 与 group_endpoint_types 收窄，不再乘倍率 |
| GET {BaseURL}/models | 模型 API Key；与所选组的真实报价路线求交 |
| GET /v1/models?mathgpt_catalog=1 | 可选 mathgpt 扩展：schema_version=1，group_verified/group/pricing 仅包含 Key 本组和获准型号，不需账号或入口密码；自动分组不猜价 |
| POST responses / chat/completions / messages | 角色指定协议，不自动退回其他模型或协议；每个结果保留来源与用量 |

错误：401 重新登录/换 Key；403 检查入口、分组或权限；402/额度类错误前往钱包；
429 停止自动重试，手动稍后重试；5xx 维护/上游暂不可用；暂停或协议不匹配拒绝。
开放但缺最终报价的模型明确标记，以账单为准；协作预算队列拒绝缺价路线。
账号 Bearer 不会作为模型 Key，网站入口密码和账号密码也不会被转换成 API Key。

## 可选提案 A：Key 本人上下文

用途：当前 /api/usage/token/ 不返回绑定 group，手动导入 Key 无法自行鉴定分组。
建议增加 GET /api/usage/token/context，沿用 TokenAuthReadOnly，仅查询该 Key 的本人投影。

返回 success/data：token_id、name、status、expires_at、group、allowed_models、
model_limits_enabled、allowed_endpoint_types、quota_unit、可选 user_quota（仅 allow_user_quota_query=true）。
不得返回完整 Key、其他 Key、上游渠道秘密、账号会话或管理权限。
缺少权限/过期/停用分别返回 401/403 与稳定 code，不以空 group 当 default。
桌面只有读到真实 group 后才标记 GroupVerified，不把当前手工选择冒充验证。

## 可选提案 B：浏览器原生登录

现有密码/TOTP 已可使用。若未来要覆盖 Passkey/人机验证，需由网站实现公开客户端的 OAuth/OIDC 授权码 + PKCE，
由浏览器完成本人验证。一次性 code、state、PKCE S256、短时授权、回环监听和精确重定向白名单不可省略；
客户端不得抓取浏览器 cookie、绕过验证码或将网页登录证明当模型 Key。
当前桌面没有声称存在这些接口，也没有注册任意生产回调。

## 可选提案 C：服务端任务硬预算

当前客户端是准入估算，网站现有有限额度 Key 在并发/在途请求下不是硬扣费封顶。
若要提供“绝不超过 ¥N”，需服务端原子预留与结算，按任务标识绑定模型、协议、
最大调用/并发/期限，并提供可审计的失败/退款和到期行为。
积分合同若无可信单次消耗上界，应返回 budget_guaranteed=false 或拒绝硬预算模式；
不能把客户端估计、可退预留或输出 Token 限制当作积分成本保证。
任何新 Key/预算创建必须沿用本人认证、额度和权限，不授管理角色。

## 可选提案 D：补全已开放分组的最终价

2026-10-26 公开快照中，default 已开放的三个模型缺少 group_pricing.default。
桌面保留这些模型及其 group_endpoint_types 明确允许的协议，提示未提供最终价；
没有从其他组、旧倍率或模型名称推算价格，也不允许进入自动预算队列。

建议现有 GET /api/pricing 对每个 enable_groups 项返回对应的最终 CNY 价格合同，
包括 billing_type、input/output/cache 价格及明确单位；积分模式需独立标明。
未知/暂停应明确给出 quote_available=false 和 reason，不用 0 冒充免费。
这属于公开报价，无需新增账号或管理权限；不能暴露渠道成本、密钥或私有路由。

这些提案只作为桌面项目的交接文件，未向其他聊天自动发送，也未改网站代码。
