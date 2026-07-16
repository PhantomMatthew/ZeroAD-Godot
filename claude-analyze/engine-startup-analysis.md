# 0 A.D. 引擎启动流程详细解析

## 启动架构概览

```
main() → EarlyInit() → InitVfs() → Init() → [InitGraphics() | InitNonVisual()] → MainLoop
```

## 1. 主入口点 (main.cpp:801)

### 1.1 平台安全检查
```cpp
// 防止以root权限运行 (Unix系统)
if (geteuid() == 0) {
    // 安全警告并退出
}
```

### 1.2 Windows平台初始化
```cpp
#if OS_WIN
wutil_Init();
wdir_watch_Init();  // 文件监控系统
#endif
```

### 1.3 核心启动流程
- **EarlyInit()** - 最早期系统初始化
- **RunGameOrAtlas()** - 主游戏或Atlas编辑器分支

## 2. 早期初始化阶段 (EarlyInit)

### 2.1 线程系统初始化
```cpp
Threading::SetMainThread();  // 标记主线程
```

### 2.2 应用程序钩子
```cpp
AppHooks hooks = {0};
hooks.bundle_logs = psBundleLogs;
hooks.get_log_dir = psLogDir;
app_hooks_update(&hooks);  // 注册错误处理回调
```

### 2.3 性能分析器
```cpp
g_Profiler2.Initialise();  // 性能监控系统
```

## 3. 虚拟文件系统初始化 (InitVfs)

### 3.1 VFS创建与挂载
```cpp
g_VFS = CreateVfs();

// 按优先级挂载各个目录
g_VFS->Mount(L"cache/", paths.Cache(), VFS_MOUNT_ARCHIVABLE, 1);
g_VFS->Mount(L"logs/", paths.Logs());
g_VFS->Mount(L"saves/", paths.UserData()/"saves");
```

### 3.2 Mod系统挂载
```cpp
MountMods(paths, GetMods(args, flags));
```

## 4. 核心系统初始化 (Init)

### 4.1 配置系统
```cpp
g_ConfigDB.Initialise();
// 加载配置文件：default.cfg, user.cfg, local.cfg
```

### 4.2 日志系统
```cpp
g_Logger = new CLogger;
```

### 4.3 SpiderMonkey引擎
```cpp
// JavaScript引擎和上下文初始化
ScriptEngine scriptEngine;
g_ScriptContext = std::make_shared<ScriptContext>("Engine");
```

### 4.4 XML处理器
```cpp
CXeromycesEngine xeromycesEngine;  // libxml2包装器
```

### 4.5 模板加载器
```cpp
g_TemplateLoader.Initialise();  // 实体模板系统
```

### 4.6 控制台系统
```cpp
g_Console = new CConsole();
```

## 5. 图形系统初始化 (InitGraphics)

### 5.1 SDL初始化
```cpp
#if MUST_INIT_X11
XInitThreads();  // X11线程安全
#endif
SDL_Init(SDL_INIT_VIDEO | SDL_INIT_TIMER | SDL_INIT_NOPARACHUTE);
```

### 5.2 视频模式设置
```cpp
g_VideoMode.Initialise(args);  // 窗口/全屏模式
```

### 5.3 OpenGL上下文创建
```cpp
// 创建OpenGL上下文，选择最佳驱动配置
// 包括多重采样、深度缓冲区等设置
```

### 5.4 渲染器初始化
```cpp
g_Renderer.Open(g_xres, g_yres);
g_Renderer.Resize(g_xres, g_yres);
```

### 5.5 GUI系统
```cpp
g_GUI = new CGUIManager{scriptContext, scriptInterface};
```

### 5.6 音频系统
```cpp
if (!g_DisableAudio)
    ISoundManager::CreateSoundManager();
```

### 5.7 输入系统
```cpp
InitInput();
g_Joystick.Initialise();
```

## 6. 子系统相互依赖关系

### 6.1 核心全局对象
```cpp
// 基础设施层
extern PIVFS g_VFS;                    // 虚拟文件系统
extern CLogger* g_Logger;              // 日志系统  
extern CConsole* g_Console;            // 控制台
extern CConfigDB g_ConfigDB;           // 配置数据库

// 脚本系统层
extern thread_local std::shared_ptr<ScriptContext> g_ScriptContext;
extern CScriptStatsTable* g_ScriptStatsTable;

// 渲染系统层
extern CRenderer g_Renderer;           // 主渲染器
extern CVideoMode g_VideoMode;         // 视频模式管理器

// 游戏逻辑层
extern CGame* g_Game;                  // 游戏实例
extern CGUIManager* g_GUI;             // GUI管理器

// 输入/音频层
extern ISoundManager* g_SoundManager; // 音频管理器
extern CJoystick g_Joystick;          // 手柄输入
```

### 6.2 依赖关系图
```
VFS (最底层)
├── ConfigDB
├── Logger  
├── ScriptContext
│   ├── TemplateLoader
│   ├── Console
│   └── GUIManager
├── VideoMode → Renderer
│   ├── SoundManager
│   └── Game
│       └── Simulation2
```

### 6.3 关键依赖顺序
1. **VFS必须最先** - 所有配置文件和资源依赖它
2. **ConfigDB依赖VFS** - 需要读取配置文件
3. **ScriptContext依赖VFS** - 需要加载JavaScript文件
4. **Renderer依赖VideoMode** - 需要OpenGL上下文
5. **Game依赖所有前面的系统** - 最高层系统

## 7. 主循环架构 (Frame函数)

### 7.1 性能监控
```cpp
g_Profiler2.RecordFrameStart();
PROFILE2("frame");
```

### 7.2 时间管理
```cpp
const double time = timer_Time();
g_frequencyFilter->Update(time);
const float realTimeSinceLastFrame = /* 计算帧时间 */;
```

### 7.3 系统更新顺序
```cpp
// 1. 文件热重载
ReloadChangedFiles();

// 2. 渐进式资源加载
ProgressiveLoad();
RendererIncrementalLoad();

// 3. 事件处理
PumpEvents();

// 4. 网络更新
if (g_NetClient) g_NetClient->Poll();

// 5. GUI更新
g_GUI->TickObjects();

// 6. 游戏逻辑更新
if (g_Game && g_Game->IsGameStarted())
    g_Game->Update(realTimeSinceLastFrame);

// 7. 音频更新
if (g_SoundManager) g_SoundManager->IdleTask();

// 8. 渲染
g_Renderer.RenderFrame(true);
```

## 8. 关键设计模式

### 8.1 单例模式
- 大多数全局系统使用单例模式
- 通过全局变量 `g_*` 访问

### 8.2 分层架构
- **平台抽象层** (lib/) - 跨平台底层功能
- **核心系统层** (ps/) - 基础设施
- **引擎层** (graphics/, renderer/) - 渲染引擎
- **脚本层** (scriptinterface/) - JavaScript集成
- **游戏层** (simulation2/, gui/) - 游戏逻辑

### 8.3 事件驱动
- SDL事件系统处理输入
- 组件间通过消息系统通信

## 9. 错误处理和恢复

### 9.1 启动错误处理
```cpp
try {
    RunGameOrAtlas({argv, argc});
} catch (const RL::SetupError&) {
    returnValue = EXIT_FAILURE;
}
```

### 9.2 重启机制
```cpp
do {
    // 初始化所有系统
    // 运行游戏循环
    // 清理资源
} while (g_Shutdown == ShutdownType::Restart);
```

### 9.3 Atlas编辑器切换
```cpp
if (g_Shutdown == ShutdownType::RestartAsAtlas)
    ATLAS_RunIfOnCmdLine(args, true);
```

## 10. 性能优化特性

### 10.1 渐进式加载
- 资源分帧加载，避免阻塞主线程
- 纹理管理器支持增量加载

### 10.2 热重载系统
- 文件监控自动重新加载资源
- 支持脚本、材质、模型热更新

### 10.3 多线程支持  
- 任务管理器 (TaskManager) 分配后台任务
- 渲染和游戏逻辑可并行处理

这个启动架构确保了系统的模块化、可维护性和扩展性，同时通过合理的依赖管理避免了循环依赖问题。

## 文件位置参考

- 主入口: `source/main.cpp:801`
- 早期初始化: `source/ps/GameSetup/GameSetup.cpp:496`
- VFS初始化: `source/ps/GameSetup/GameSetup.cpp:199`
- 核心初始化: `source/ps/GameSetup/GameSetup.cpp:537`
- 图形初始化: `source/ps/GameSetup/GameSetup.cpp:630`
- 主循环: `source/main.cpp:386`