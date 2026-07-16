# 0 A.D. 中使用 SpiderMonkey 开发 Mod 详细指南

## Mod 系统架构

### 1. Mod 目录结构
```
binaries/data/mods/your_mod/
├── mod.json              # Mod 元数据和依赖
├── art/                  # 艺术资源（模型、纹理等）
├── audio/               # 音频文件
├── globalscripts/       # 全局 JavaScript 脚本
├── gui/                 # 用户界面
├── simulation/          # 游戏逻辑核心
│   ├── components/      # ECS 组件 (JavaScript)
│   ├── data/           # 游戏数据 (JSON)
│   ├── helpers/        # 辅助函数
│   └── templates/      # 实体模板 (XML)
└── l10n/               # 本地化文件
```

### 2. SpiderMonkey 集成层次

**C++ 引擎 ↔ SpiderMonkey ↔ JavaScript 脚本**

- C++ 通过 `JSInterface_*` 类暴露功能给 JavaScript
- JavaScript 通过全局对象 `Engine` 调用 C++ 功能
- 双向数据绑定和事件系统

## 开发样例：创建自定义单位组件

### 1. 创建 mod.json
```json
{
    "name": "my_custom_mod",
    "version": "1.0.0", 
    "label": "My Custom Mod",
    "description": "A demo mod showcasing SpiderMonkey integration",
    "dependencies": ["public"]
}
```

### 2. 创建自定义组件 - 燃烧效果
`simulation/components/Burning.js`:
```javascript
function Burning() {}

// 定义组件的 XML Schema
Burning.prototype.Schema =
    "<a:help>Makes units burn and take damage over time.</a:help>" +
    "<a:example>" +
        "<BurnDamage>5</BurnDamage>" +
        "<BurnInterval>1000</BurnInterval>" +
        "<Duration>10000</Duration>" +
    "</a:example>" +
    "<element name='BurnDamage' a:help='Damage per interval'>" +
        "<data type='positiveInteger'/>" +
    "</element>" +
    "<element name='BurnInterval' a:help='Time between damage in milliseconds'>" +
        "<data type='positiveInteger'/>" +
    "</element>" +
    "<element name='Duration' a:help='Total burn duration in milliseconds'>" +
        "<data type='positiveInteger'/>" +
    "</element>";

Burning.prototype.Init = function()
{
    // 组件初始化
    this.burnDamage = +this.template.BurnDamage;
    this.burnInterval = +this.template.BurnInterval;
    this.duration = +this.template.Duration;
    this.remainingTime = this.duration;
    this.lastDamageTime = 0;
    
    // 获取其他需要的组件
    var cmpHealth = Engine.QueryInterface(this.entity, IID_Health);
    if (!cmpHealth)
    {
        error("Burning component requires Health component");
        return;
    }
    
    // 启动燃烧计时器
    var cmpTimer = Engine.QueryInterface(SYSTEM_ENTITY, IID_Timer);
    this.timer = cmpTimer.SetInterval(this.entity, IID_Burning, "DoBurnDamage", 
                                     this.burnInterval, this.burnInterval, {});
};

Burning.prototype.DoBurnDamage = function()
{
    // 减少剩余时间
    this.remainingTime -= this.burnInterval;
    
    if (this.remainingTime <= 0)
    {
        // 燃烧结束
        this.StopBurning();
        return;
    }
    
    // 造成伤害
    var cmpHealth = Engine.QueryInterface(this.entity, IID_Health);
    if (cmpHealth && !cmpHealth.IsUnhealable())
    {
        cmpHealth.Reduce(this.burnDamage);
        
        // 播放燃烧特效
        var cmpPosition = Engine.QueryInterface(this.entity, IID_Position);
        if (cmpPosition && cmpPosition.IsInWorld())
        {
            var pos = cmpPosition.GetPosition();
            Engine.PostMessage(this.entity, MT_PlayEffectAtPosition, 
                             { "effectName": "flame.xml", "position": pos });
        }
    }
};

Burning.prototype.StopBurning = function()
{
    if (this.timer)
    {
        var cmpTimer = Engine.QueryInterface(SYSTEM_ENTITY, IID_Timer);
        cmpTimer.CancelTimer(this.timer);
        this.timer = undefined;
    }
    
    // 移除组件
    Engine.DestroyComponent(this.entity, IID_Burning);
};

// 序列化支持（用于存档）
Burning.prototype.Serialize = function()
{
    return {
        "remainingTime": this.remainingTime,
        "timer": this.timer
    };
};

Burning.prototype.Deserialize = function(data)
{
    this.Init();
    this.remainingTime = data.remainingTime;
    this.timer = data.timer;
};

// 注册组件接口
Engine.RegisterComponentType(IID_Burning, "Burning", Burning);
```

### 3. 创建接口定义
`simulation/components/interfaces/Burning.js`:
```javascript
// 定义组件接口ID
var IID_Burning = 200; // 使用未占用的ID

// 定义相关消息类型
var MT_PlayEffectAtPosition = "PlayEffectAtPosition";
```

### 4. 创建使用燃烧组件的单位模板
`simulation/templates/units/fire_warrior.xml`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Entity parent="template_unit_infantry_melee_spearman">
  <Identity>
    <GenericName>Fire Warrior</GenericName>
    <SpecificName>Pyro Hoplite</SpecificName>
    <Icon>units/fire_warrior.png</Icon>
  </Identity>
  
  <Burning>
    <BurnDamage>3</BurnDamage>
    <BurnInterval>500</BurnInterval>
    <Duration>15000</Duration>
  </Burning>
  
  <Attack>
    <Melee>
      <AttackName>Flaming Spear</AttackName>
      <Damage>
        <Hack>12</Hack>
        <Fire>5</Fire>
      </Damage>
      <MaxRange>4</MaxRange>
      <RepeatTime>1000</RepeatTime>
    </Melee>
  </Attack>
  
  <VisualActor>
    <Actor>units/fire_warrior.xml</Actor>
  </VisualActor>
</Entity>
```

### 5. 扩展现有组件 - 修改攻击系统
`simulation/components/Attack.js` (通过mod系统扩展):
```javascript
// 在现有Attack组件基础上添加燃烧效果
Attack.prototype.PerformAttack = function(type, target)
{
    // 调用原始攻击逻辑...
    // ... 原有代码 ...
    
    // 添加燃烧效果
    if (this.template[type].Damage && this.template[type].Damage.Fire)
    {
        var cmpBurning = Engine.QueryInterface(target, IID_Burning);
        if (!cmpBurning)
        {
            // 为目标添加燃烧组件
            Engine.AddComponent(target, IID_Burning, {
                "BurnDamage": Math.floor(+this.template[type].Damage.Fire),
                "BurnInterval": "1000",
                "Duration": "8000"
            });
        }
    }
};
```

### 6. 创建全局脚本工具
`globalscripts/BurningUtils.js`:
```javascript
/**
 * 燃烧效果工具函数
 */
var BurningUtils = {
    /**
     * 让指定实体开始燃烧
     */
    IgniteEntity: function(entity, damage, duration)
    {
        var cmpBurning = Engine.QueryInterface(entity, IID_Burning);
        if (cmpBurning)
            return false; // 已经在燃烧
            
        Engine.AddComponent(entity, IID_Burning, {
            "BurnDamage": damage || "5",
            "BurnInterval": "1000", 
            "Duration": duration || "10000"
        });
        return true;
    },
    
    /**
     * 区域燃烧效果
     */
    IgniteArea: function(position, radius, damage, duration)
    {
        var cmpRangeManager = Engine.QueryInterface(SYSTEM_ENTITY, IID_RangeManager);
        var entities = cmpRangeManager.ExecuteQuery(INVALID_ENTITY, 
                                                    position.x - radius, position.z - radius,
                                                    position.x + radius, position.z + radius,
                                                    [], IID_Health);
        
        for (let entity of entities)
        {
            this.IgniteEntity(entity, damage, duration);
        }
    }
};
```

### 7. GUI 集成 - 显示燃烧状态
`gui/session/unit_panels.js`:
```javascript
// 在单位面板中显示燃烧状态
function updateUnitStatusIcons(unitEntState)
{
    // ... 现有代码 ...
    
    // 检查燃烧状态
    if (unitEntState.burning)
    {
        let burningIcon = Engine.GetGUIObjectByName("burningStatusIcon");
        if (burningIcon)
        {
            burningIcon.hidden = false;
            burningIcon.tooltip = sprintf(translate("Burning: %(time)s seconds remaining"), 
                                        { time: Math.ceil(unitEntState.burning.remainingTime / 1000) });
        }
    }
}
```

### 8. 控制台命令 (开发/调试用)
```javascript
// 在游戏控制台中可以使用的命令
if (Engine.IsDebugBuild())
{
    global.IgniteSelected = function()
    {
        var selected = g_Selection.toList();
        for (let entity of selected)
        {
            BurningUtils.IgniteEntity(entity, 10, 15000);
        }
    };
}
```

## 关键开发要点

### 1. **C++/JavaScript 交互**
- 使用 `Engine.*` 全局函数调用 C++ 功能
- 通过 `Engine.QueryInterface()` 获取组件接口
- 使用 `Engine.PostMessage()` 发送消息

### 2. **组件生命周期**
```javascript
Init()           // 组件创建时调用
Serialize()      // 保存游戏状态
Deserialize()    // 加载游戏状态  
OnDestroy()      // 组件销毁时清理
```

### 3. **性能优化**
- 避免频繁的 `QueryInterface` 调用
- 使用对象池减少垃圾回收
- 批量处理实体操作

### 4. **调试技巧**
```javascript
// 使用游戏内置的日志系统
warn("Debug message: " + someVariable);
error("Error occurred!");

// 条件编译
if (Engine.IsDebugBuild())
{
    // 仅在调试版本中执行
}
```

## SpiderMonkey 技术选择分析

### 为什么在当时是好选择
- 项目开始时（~2010年）是最佳选择之一
- JavaScript对modder友好
- Mozilla长期支持保证
- 已有大量代码投资

### 现代替代方案
1. **V8** - 更高性能，但集成复杂
2. **Lua** - 专为嵌入设计，更轻量
3. **WebAssembly** - 接近原生性能
4. **AngelScript** - 专门为游戏设计

### 建议
对于0 A.D.这样的成熟项目，继续使用SpiderMonkey是合理的，但可以考虑升级到更新版本以获得性能改进。

这个架构允许开发者用 JavaScript 快速创建复杂的游戏逻辑，同时保持与 C++ 引擎的高性能集成。SpiderMonkey 提供了稳定的 JS 执行环境，使得 mod 开发既灵活又强大。

## 相关文件参考

- Mod结构: `binaries/data/mods/public/mod.json`
- 组件系统: `binaries/data/mods/public/simulation/components/`
- 全局脚本: `binaries/data/mods/public/globalscripts/`
- 实体模板: `binaries/data/mods/public/simulation/templates/`
- GUI系统: `binaries/data/mods/public/gui/`
- JavaScript接口: `source/scriptinterface/`