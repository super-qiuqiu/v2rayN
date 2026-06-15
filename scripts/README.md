# v2rayN Build Scripts

## 脚本说明

这些脚本用于快速编译、打包和启动 v2rayN macOS 版本。

### 可用脚本

1. **`rebuild-and-launch-macos.sh`** （推荐）
   - 自动检测 CPU 架构（x64 或 ARM64）
   - 调用对应的构建脚本

2. **`rebuild-and-launch-macos-x64.sh`**
   - 构建 Intel Mac (x86_64) 版本

3. **`rebuild-and-launch-macos-arm64.sh`**
   - 构建 Apple Silicon (ARM64) 版本

## 使用方法

### 快速启动（自动检测架构）

```bash
cd /Users/qiuqiu/pj/v2rayN
bash scripts/rebuild-and-launch-macos.sh
```

### 指定架构编译

```bash
# Intel Mac
bash scripts/rebuild-and-launch-macos-x64.sh

# Apple Silicon
bash scripts/rebuild-and-launch-macos-arm64.sh
```

## 脚本功能

每个脚本会执行以下步骤：

1. **[1/5] 构建项目**
   - 使用 .NET 10 编译 v2rayN.Desktop 项目
   - 目标平台：macOS (osx-x64 或 osx-arm64)
   - 配置：Release

2. **[2/5] 创建 macOS .app 包**
   - 创建标准 .app 目录结构
   - 复制 self-contained 发布产物
   - 生成 Info.plist

3. **[3/5] 停止运行中的进程**
   - 停止旧版 v2rayN GUI
   - 停止代理核心 (sing-box, xray, mihomo)
   - 优雅关闭，失败则强制终止

4. **[4/5] 安装到 /Applications**
   - 删除旧版本
   - 复制新版本到 /Applications/v2rayN.app
   - 启动应用

5. **[5/5] 验证启动**
   - 等待进程启动（最多 5 秒）
   - 显示进程 PID
   - 提示新功能可用

## 前置要求

### 必需

- macOS 10.15+
- .NET 10 SDK（仅构建时需要）
- Git（用于子模块）

### 安装 .NET 10

```bash
# 方法 1: 官方安装脚本
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet

# 方法 2: Homebrew (需要预览版)
brew install --cask dotnet-sdk@preview
```

### 初始化子模块

```bash
cd /Users/qiuqiu/pj/v2rayN
git submodule update --init --recursive
```

## 构建输出

构建完成后，文件位于：

- **x64**: `v2rayN/v2rayN.Desktop/bin/Release/net10.0/osx-x64/publish/`
- **ARM64**: `v2rayN/v2rayN.Desktop/bin/Release/net10.0/osx-arm64/publish/`
- **安装位置**: `/Applications/v2rayN.app`

## 新功能验证

启动后，检查菜单栏是否有新选项：

```
菜单栏 -> Exit and keep core running
```

点击后：
- GUI 窗口关闭
- 代理核心继续运行
- 系统代理保持当前配置
- 代理端口仍可用

验证核心运行：
```bash
ps aux | grep -E "sing-box|xray"
curl --proxy socks5://127.0.0.1:7890 https://www.google.com
```

## 故障排查

### 编译失败

```bash
# 检查 .NET 版本
dotnet --version  # 应该显示 10.0.x

# 检查子模块
ls v2rayN/GlobalHotKeys/  # 应该有内容

# 重新初始化
git submodule update --init --recursive
```

### 应用无法启动

```bash
# 查看日志
tail -f ~/Library/Logs/v2rayN/*.log

# 检查权限
ls -la /Applications/v2rayN.app/Contents/MacOS/v2rayN
```

### CPU 架构不匹配

```bash
# 检查当前架构
uname -m

# x86_64 → 使用 x64 脚本
# arm64  → 使用 arm64 脚本
```

## 目录结构

```
v2rayN/
├── scripts/
│   ├── rebuild-and-launch-macos.sh           # 自动检测架构
│   ├── rebuild-and-launch-macos-x64.sh       # Intel Mac
│   ├── rebuild-and-launch-macos-arm64.sh     # Apple Silicon
│   └── README.md                              # 本文档
├── v2rayN.Desktop/
│   └── bin/Release/net10.0/
│       ├── osx-x64/publish/                   # x64 构建输出
│       └── osx-arm64/publish/                 # ARM64 构建输出
└── ...
```

## 贡献

修改源码后，使用这些脚本快速测试：

1. 修改代码
2. 运行 `bash scripts/rebuild-and-launch-macos.sh`
3. 应用自动重启，无需手动操作

---

**生成时间**: 2026-06-15
