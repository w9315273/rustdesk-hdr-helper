# Rustdesk HDR Helper

一个适合用 RustDesk 远程控制自己电脑的 Windows HDR 小工具.

被控端 Windows 开启 HDR 时, 控制端看到的远程画面可能出现色彩异常, 例如颜色发灰, 发白或亮度不正常.本工具通过在远控期间自动关闭被控端 HDR, 帮助避免这类问题.

- 检测到 RustDesk 连接时, 自动关闭 HDR.
- 所有远控连接断开约 5 秒后, 自动重新开启 HDR.
- 支持通过右下角托盘菜单手动开启或关闭 HDR.

适合平时在自己的电脑上使用 HDR, 出门后又需要远程连接的场景, 减少每次手动切换的麻烦.

## 使用方法

从 [Releases](https://github.com/w9315273/rustdesk-hdr-helper/releases) 下载 `RustDeskHDRhelper.exe`, 在**被控端 Windows 电脑**上运行并允许管理员权限. 需已安装并运行 RustDesk 服务, 工具通过服务日志检测连接; 如果启动时已经在远控, 请断开后重新连接一次.

需要管理员权限的原因: 工具需读取 Windows 受保护的 LocalService 服务账户目录下的 RustDesk 日志, 以检测远控连接和断开状态.
