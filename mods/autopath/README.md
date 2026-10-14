# AutoPath 1.2.0（v3）

客户端插件。人形沿游戏生成的地面路线行走；蝙蝠飞行时直接沿水平直线到达目标上空。游戏自身的飞行高度、区域边界、碰撞及落地合法性仍生效。

## 安装与使用

1. 客户端已安装适合 V Rising 1.1.13 r99712 的 BepInEx 6 IL2CPP。服务器无需安装本插件。
2. 正常退出客户端，将旧版自动寻路 DLL 移入自己的备份目录，避免多个版本同时加载。不要动其他插件。
3. 将 `plugins/1014_VRisingAutoPath_v3.dll` 复制到客户端的 `BepInEx/plugins/`。
4. 启动游戏，配置自动生成在 `BepInEx/config/local.vrising.autopath.cfg`。示例在同目录 `config.example.cfg`，首次安装无需手动复制。
5. 在已能正常飞行的蝙蝠形态或普通人形下按 M 打开地图。按住 Shift，在地图背景左键点击目标，然后松开 Shift 和鼠标；地图会自动关闭。
6. 蝙蝠到点后停止水平移动并保持形态，需手动落地。WASD 或 F8 取消；攻击、技能、菜单、变身操作和失焦也可取消。

保持游戏焦点；不要同时使用其他自动控制移动的插件或脚本。真正无法生成或补齐的地面路线会停止并提示原因。初始短暂出现“正在等待游戏生成路线”是共用提示；确认蝙蝠飞行后不会等待地面路线。

## 卸载与排错

退出游戏后将本插件 DLL 移出 plugins 并保留备份即可。旧配置可另行备份，不要覆盖其他插件配置。

查看客户端 `BepInEx/LogOutput.log` 中 `[AUTOPATH]` 行。核对 Enabled=true、游戏版本、是否为蝙蝠飞行；在地图图标或菜单控件上点击不一定是有效目标。

## 构建与算法测试

在 PowerShell 中执行，路径换成自己的游戏安装目录：

```powershell
& ./src/1014_构建_v3.ps1 -GamePath '<客户端目录>' -OutputPath './.validate/autopath'
& ./src/1014_测试_v3.ps1
```

依赖程序集从指定游戏目录的 BepInEx/core 和 BepInEx/interop 读取，不随源码分发。
