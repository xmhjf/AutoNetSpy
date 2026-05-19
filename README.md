# AutoNetSpy

AutoNetSpy 是一个基于 WinForms 的 .NET 程序集批量反编译工具，底层调用 `ilspycmd`。

## 功能

- 扫描目录下的 `.dll` / `.exe`，自动识别托管程序集
- 树形展示程序集并支持勾选反编译目标
- 并行批量反编译，支持取消
- 支持自动安装 `ilspycmd`（`dotnet tool install -g ilspycmd`）
- 可选生成 `.csproj`
- 可选清理编译器生成文件
- 可选跳过资源目录
- 支持按名称前缀过滤（如 `System`、`Microsoft`）
- 支持“跳过已反编译”
- 输出 `_summary.json` 和 `_logs` 便于追踪结果

## 技术栈

- .NET 10（`net10.0-windows`）
- Windows Forms
- `System.Reflection.Metadata` / `PEReader` 用于程序集识别
- `ilspycmd` 用于反编译

## 运行环境

- Windows
- 已安装 .NET 10 SDK
- 建议可访问 NuGet（首次自动安装 `ilspycmd` 时需要）

## 快速开始

```bash
dotnet restore
dotnet run --project AutoNetSpy/AutoNetSpy.csproj
```

启动后流程：

1. 选择“源目录”（待扫描程序集目录）
2. 选择“输出目录”
3. 点击“扫描”
4. 在树中勾选目标程序集
5. 配置反编译选项并点击“开始反编译”

## 主要选项说明

- **生成 .csproj 项目**：为每个程序集输出项目结构
- **清理编译器生成代码**：删除 `<Module>`、`<PrivateImplementationDetails>` 等文件
- **跳过资源**：删除输出中的 `Resources` 目录
- **跳过已反编译**：输出目录已有 `.cs` 或 `.csproj` 时不重复处理
- **最小尺寸(KB)**：过滤体积较小程序集
- **并行度**：控制并发反编译任务数
- **跳过名称前缀**：按前缀过滤程序集（支持分号或换行分隔）

## 输出说明

- 每个程序集对应一个独立输出目录
- `_summary.json`：本次任务汇总（成功/失败/跳过、耗时等）
- `_logs/`：详细执行日志和错误日志

## 注意事项

- 本工具仅处理合法来源程序集，请遵守相关法律与许可证要求
- 反编译结果可读性受目标程序集混淆、裁剪、AOT 等因素影响
- 若安装 `ilspycmd` 后仍未识别，重启应用后重试
