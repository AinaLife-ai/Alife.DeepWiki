# Alife.DeepWiki

DeepWiki MCP客户端：查询GitHub仓库Wiki文档、向仓库提问、搜索仓库

## 功能

- 向指定GitHub仓库的DeepWiki提问，获取基于Wiki文档的答案
- 搜索GitHub仓库
- 读取仓库的DeepWiki结构
- 读取指定路径的Wiki内容

## 安装

将 `DeepWiki` 文件夹放入 Alife 的 `Plugins` 目录，同步环境后启用模块即可。

## 配置

| 配置项 | 说明 |
|---|---|
| McpUrl | DeepWiki MCP服务器URL |
| ProtocolVersion | MCP协议版本 |
| Timeout | HTTP请求超时时间(秒) |
| MaxRetries | 瞬时故障最大重试次数 |
| CacheTtl | 回答缓存有效期(秒)，0为不缓存 |
| GithubToken | 用于搜索仓库的GitHub Token(可选) |
| MaxSearchResults | 仓库搜索最大返回条数 |
| CommandWords | 触发命令词，逗号分隔 |
| EnableCommand | 是否启用/dw命令拦截 |
| DefaultRepoPresets | 默认绑定的仓库，逗号分隔 |
| ResetKeepsPresetRepo | clear后是否保持预设仓库绑定 |
| QqRichTextMode | 富文本模式：off=原样 sanitize=去Markdown stylize=全角美化 |
| EnableAutoForward | 超长内容是否用合并转发 |
| ForwardThreshold | 超过该长度触发合并转发 |
| ForceForwardAll | 所有内容都走合并转发 |