# Alife.DeepWiki

DeepWiki MCP客户端：查询GitHub仓库Wiki文档、向仓库提问、搜索仓库

## 功能

- 向指定GitHub仓库的DeepWiki提问，获取基于Wiki文档的答案
- 多路融合搜索GitHub仓库（名称/描述/话题/通用）
- 读取仓库的DeepWiki文档结构
- 读取指定主题的Wiki内容
- /dw 命令系统：关键词搜索、数字选候选、owner/repo直查、状态查询、上下文清除
- 上下文管理：按会话或按用户隔离，预设仓库绑定
- 富文本处理：Markdown转QQ友好格式（sanitize/stylize/off三种模式）
- 缓存机制：回答缓存+搜索缓存
- 瞬时故障自动重试（429/5xx指数退避）

## 安装

将 `DeepWiki` 文件夹放入 Alife 的 `Plugins` 目录，同步环境后启用模块即可。

## 命令用法

```
/dw <关键词>          搜索GitHub仓库，返回候选列表
/dw 1                 从候选列表选择第1个仓库
/dw owner/repo        直接指定仓库
/dw owner/repo 问题   直接向指定仓库提问
/dw <问题>            向当前上下文仓库追问
/dw ?                 查看当前上下文仓库
/dw clear             清除上下文
```

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
| UseMultiPathSearch | 多路融合搜索 |
| CommandWords | 触发命令词，逗号分隔 |
| EnableCommand | 是否启用/dw命令拦截 |
| DefaultQuestion | 选定仓库后自动提问的默认问题 |
| IsolateContextByUser | 按用户隔离上下文 |
| DefaultRepoPresets | 默认仓库预设（格式：会话标识;owner/repo） |
| ResetKeepsPresetRepo | clear后是否保持预设仓库绑定 |
| LlmBindPresetRepo | LLM自然语言调用时默认绑定预设仓库 |
| ClearCommandWords | 清除上下文命令词 |
| StatusCommandWords | 状态查询命令词 |
| EnableLlmTool | 是否注册LLM工具 |
| EnableAutoForward | 超长内容是否用合并转发 |
| ForceForwardAll | 所有内容都走合并转发 |
| UseLengthThreshold | 启用长度判断触发转发 |
| ForwardThreshold | 超过该长度触发合并转发 |
| ForwardNodeMaxChars | 合并转发单节点最大字符数 |
| ForwardApiTimeout | 合并转发API超时(秒) |
| EnableForwardPlainFallback | 合并转发失败后降级为普通消息 |
| QqRichTextMode | 富文本模式：off=原样 sanitize=去Markdown stylize=全角美化 |
| AppendOperationGuide | 答案末尾附加操作指南 |

## 致谢

原版插件：[znq19/KiraAI_deepwiki_plugin](https://github.com/znq19/KiraAI_deepwiki_plugin)
