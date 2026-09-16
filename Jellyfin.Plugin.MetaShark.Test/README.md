# 单集标题覆盖回归验证

## 问题与修复边界

在已检查的 Jellyfin 12 中，“搜索缺失元数据”（`FullRefresh`、`ReplaceAllMetadata=false`）会先运行文件探测。启用内嵌标题时，宿主 `FFProbeVideoInfo.FetchEmbeddedInfo` 直接把文件 `title` 写入已有单集的 `Name`，随后最终元数据合并又保留这个非空名称。即使 MetaShark 返回正确单集名，最终仍可能存成文件中内嵌的整剧名。

实测样例包括《爱书的下克上》TMDb 91768 S04E18 和《地狱模式》TMDb 280049 S02E01：内嵌 `title` 与错误显示的繁体整剧名一致。Jellyfin 10.10.7 的最终合并在该模式下会替换字段，因而可由远程刮削标题写回。

兼容修复只在启用了 MetaShark 元数据和内嵌标题的库中，围绕宿主 `ProbeProvider` 保存、恢复已有的单集标题；使用真实刷新选项，不依赖 HTTP 请求上下文。恢复发生在本地/远程元数据合并之前，不修改语言选择、文件名解析、剧集组、LLM、NFO 优先级或默认标题回填规则，也不修改媒体库配置及用户锁定字段。

此修复防止再次覆盖，不猜测或批量改写历史上已经损坏的标题。

## 常规回归

在仓库根目录运行：

```sh
dotnet test Jellyfin.Plugin.MetaShark.Test/Jellyfin.Plugin.MetaShark.Test.csproj \
  -p:DoILRepack=false --filter 'TestCategory=Stable'
```

## 真实宿主代码回归

将 `JellyfinServerPath` 指向包含 `MediaBrowser.Providers.dll` 的 Jellyfin 12 程序目录：

```sh
dotnet test Jellyfin.Plugin.MetaShark.Test/Jellyfin.Plugin.MetaShark.Test.csproj \
  -p:DoILRepack=false -p:JellyfinServerPath=/path/to/jellyfin \
  --filter 'TestCategory=HostCompatibility'
```

这些可选测试执行真实宿主的内嵌元数据导入和最终合并，同时使用真实 MetaShark provider 与缓存样例；媒体项和持久化接口均为隔离测试对象，不连接运行中的 Jellyfin，不需要服务器地址或密钥。未指定宿主目录时不编译这组测试，也不会给插件新增宿主程序集依赖。

覆盖修复前复现、修复后标题保留、简介补缺、默认标题回填开关、缺少标题翻译、覆盖刷新、自动刷新、远程失败、缺少剧集 ID、锁定 NFO 和单集标题锁定。
