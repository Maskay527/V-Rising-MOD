# MoveSpeed 6.0.0（v6）

服务器插件。人物、变身和骑乘使用同一倍率；每个玩家独立管理原生修改来源，处理退出形态、配置重载及已保存来源接管。插件默认倍率 **1.5**，允许0.1至10；

## 安装与配置

1. 服务器已安装适合 V Rising 1.1.13 r99712 的 BepInEx 6 IL2CPP。客户端不需要安装本移速插件。
2. 正常停止服务器，将 `plugins/1014_VRisingMoveSpeed_v6.dll` 复制到服务器 `BepInEx/plugins/`，启动服务器。
4. 配置在 `BepInEx/config/dsh.vrising.movespeed.cfg`，默认自动生成；已有配置沿用原值。示例为 `config.example.cfg`。

```ini
[1. General]
Enabled = true
SpeedMultiplier = 1.5

[2. Advanced]
ScaleMountAcceleration = true

[3. Debug]
LogMovement = false
```

配置在服务器运行时自动重读。LogMovement=true可记录速度和形态诊断，日志可能含角色名称，分享前自行脱敏。插件不修改永久坐骑属性；转向速度不变。

## 卸载、回退与兼容

先把 Enabled 改为 false，在服务器世界运行时等待配置重读和恢复原速，正常保存并停止服务器，再把 DLL 移到备份目录。不要直接从运行中的服务器删除插件。回退时使用同样流程后替换为备份版本。

其他也改写移速或变身速度的插件可能相互影响，需要单独验证。日志位于服务器 `BepInEx/LogOutput.log`，检索 `[MoveSpeed v6]`。专用服务器及本机测试世界已验证，远端云服部署未验证。

## 构建与检查

```powershell
& ./src/1014_构建_v6.ps1 -ServerPath '<服务器目录>' -OutputPath './.validate/movespeed'
```

需要 .NET SDK、.NET 6 runtime，以及服务器自己的 BepInEx/core 和 interop 程序集。构建脚本同时运行纯速度算法测试。不会自动启动服务器、载入或修改存档。
