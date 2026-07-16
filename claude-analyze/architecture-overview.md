# 0 A.D. 项目架构总览

## 项目基本信息
- **项目名称:** 0 A.D. (Zero A.D.)
- **类型:** 开源实时战略游戏
- **主要语言:** C++ (引擎) + JavaScript (游戏逻辑)
- **版本:** 0.28.0
- **许可证:** GPL 2.0+

## 代码库结构

### 顶级目录
```
0ad/
├── binaries/          # 游戏数据和资源文件
│   └── data/mods/    # Mod系统 (public, mod等)
├── build/            # 构建系统和工具
│   ├── premake/      # Premake5构建脚本
│   └── workspaces/   # 生成的项目文件
├── docs/             # 技术文档
├── libraries/        # 第三方库依赖
├── source/           # 引擎源代码
└── claude-analyze/   # Claude分析文档 (新增)
```

### 核心引擎模块 (source/)
```
source/
├── lib/              # 平台抽象层
│   ├── sysdep/      # 系统依赖代码
│   ├── file/        # 文件系统抽象
│   └── allocators/  # 内存管理
├── ps/               # 核心系统
│   ├── GameSetup/   # 游戏启动和初始化
│   ├── XML/         # XML处理
│   └── XMB/         # 二进制XML格式
├── scriptinterface/  # JavaScript集成
├── renderer/         # 渲染引擎
│   └── backend/     # 渲染后端抽象
├── graphics/         # 图形系统
├── simulation2/      # ECS游戏逻辑
│   ├── components/  # 游戏组件
│   ├── system/      # ECS系统核心
│   └── helpers/     # 辅助函数
├── gui/              # 用户界面
├── network/          # 网络多人游戏
├── soundmanager/     # 音频系统
├── maths/            # 数学库
├── i18n/             # 国际化
└── tools/atlas/      # 地图编辑器
```

## 技术架构

### 分层架构
1. **平台抽象层** (lib/) - 跨平台底层功能
2. **核心系统层** (ps/) - 基础设施服务
3. **引擎服务层** (graphics/, renderer/, sound/) - 引擎功能
4. **脚本集成层** (scriptinterface/) - JavaScript桥接
5. **应用逻辑层** (simulation2/, gui/) - 游戏功能

### ECS架构 (Entity-Component-System)
- **实体(Entity):** 游戏对象标识符
- **组件(Component):** 数据和行为 (JavaScript实现)
- **系统(System):** 组件管理和更新逻辑

### JavaScript集成
- **引擎:** SpiderMonkey
- **接口层:** JSInterface_* 类
- **脚本类型:** 组件、全局脚本、GUI脚本
- **数据交换:** C++/JS双向绑定

## 关键技术特性

### 1. 虚拟文件系统 (VFS)
- 统一的文件访问接口
- 支持档案文件和目录混合挂载
- Mod系统的基础架构

### 2. 热重载系统
- 实时文件监控
- 脚本、材质、模型自动更新
- 开发效率优化

### 3. 模块化渲染
- OpenGL/Vulkan后端支持
- 现代渲染管线
- 多种后处理效果

### 4. 组件式架构
- 灵活的实体系统
- JavaScript组件开发
- 模块化游戏逻辑

## 构建和开发流程

### 构建系统
- **生成器:** Premake5
- **目标:** Visual Studio, Xcode, Make
- **配置:** build/premake/premake5.lua

### 代码质量工具
- **C++:** 内置测试框架
- **JavaScript:** ESLint + 自定义规则
- **Python:** ruff 格式化和检查

### 开发工具
- **地图编辑器:** Atlas (集成)
- **性能分析:** 内置Profiler2系统
- **调试:** SpiderMonkey调试支持

## 依赖库

### 核心依赖
- **SDL2** - 平台抽象和输入
- **SpiderMonkey** - JavaScript引擎
- **OpenGL** - 图形渲染
- **OpenAL** - 音频处理
- **libxml2** - XML解析
- **zlib** - 数据压缩

### 可选依赖
- **NVTT** - 纹理处理
- **gloox** - XMPP聊天支持
- **curl** - HTTP客户端
- **miniupnpc** - UPnP端口映射

## 平台支持

### 主要平台
- **Windows** - DirectX, MSVC支持
- **Linux** - OpenGL, GCC/Clang
- **macOS** - Metal后端, Xcode

### 构建要求
- C++17 标准
- Python 3.11+ (工具脚本)
- Node.js 20+ (JavaScript工具)

## 性能特性

### 渲染优化
- 批处理渲染
- 实例化渲染
- 多线程资源加载
- GPU计算着色器

### 内存管理
- 自定义分配器
- 对象池技术
- 智能指针使用

### 网络优化
- 确定性模拟
- 命令同步架构
- 延迟补偿

## 扩展性设计

### Mod支持
- 完整的JavaScript API
- 资源替换系统
- 模板继承机制

### 脚本系统
- 热重载支持
- 错误恢复机制
- 性能监控

### 插件架构
- 组件注册系统
- 事件钩子机制
- 动态加载支持

## 社区和贡献

### 开发社区
- 论坛: wildfiregames.com/forum
- 代码仓库: gitea.wildfiregames.com
- IRC: #0ad on QuakeNet

### 贡献方式
- 代码贡献 (C++/JavaScript)
- 艺术资源创作
- 本地化翻译
- 文档改进

---

这个架构文档提供了0 A.D.项目的全貌概览，适合新开发者快速理解项目结构和技术选型。