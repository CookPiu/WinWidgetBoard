# ADR-0031：CPU 频率改由每核 PDH 计数器测量，脱离传感器层

状态：Accepted
日期：2026-08-29
关系：取代 [ADR-0028](0028-system-monitor-scope-and-sensor-tiers.md) 第 3 条「CPU 频率归入传感器层」；
修正 [ADR-0029](0029-drop-the-bundled-sensor-driver.md) 第 3 条中关于 CPU 频率的部分，
温度与风扇两项结论不变

## 背景

[ADR-0028](0028-system-monitor-scope-and-sensor-tiers.md) 第 3 条把 CPU 频率和温度、风扇一起
归入需要内核驱动的传感器层，[ADR-0029](0029-drop-the-bundled-sensor-driver.md) 随后放弃该层，
于是这四项恒显示「此电脑无法读取」。用户要求补齐 CPU 频率。

ADR-0028 的判断建立在两条实测上，两条都成立，但两条测的都不是可用的口径：

- `\Processor Information(_Total)\Processor Frequency` 恒为 1.33 GHz；
- `% Processor Performance ×` 注册表 `~MHz` 得到 8.18 GHz。

## 验证结果

参考机（Windows 11 26200，Intel Core Ultra 5 225H，4 P-core + 8 E-core + 2 LPE-core）
**按实例**读取同一对计数器：

| 实例 | `Processor Frequency` | 说明 |
| --- | --- | --- |
| `_Total` / `0,_Total` | 1328 MHz | 三类核标称值的平均，不对应任何一颗核 |
| `0,0` `0,1` `0,10` `0,11` | 1700 MHz | P-core 标称 |
| `0,2`…`0,9` | 1300 MHz | E-core 标称 |
| `0,12` `0,13` | 700 MHz | LPE-core 标称 |

也就是说 `Processor Frequency` 并非「恒定的错值」，它是**每颗核的标称频率**，只有 `_Total`
汇总实例因为跨核类求平均而无意义。同一实例上 `% Processor Performance` 是该核的
APERF/MPERF 比值，两者相乘即该核在采样区间内的平均频率。

实测（12 个满载线程，同一时刻）：最快核 4.59 GHz，全核平均 3.77 GHz；空载时最快核 3.62 GHz。
该型号 P-core 最大睿频 4.9 GHz，读数全程落在合理区间，与机上第三方组件同量级。

同时复核了两条不可行的路径：`CallNtPowerInformation(ProcessorInformation)` 的 `CurrentMhz`
在本机满载与空载都等于 `MaxMhz`（即标称值），不随频率变化；
`Win32_Processor.CurrentClockSpeed` 同样恒为 1700。二者都不能用。

## 决策

1. **CPU 频率退出传感器层**，由 `\Processor Information(*)\Processor Frequency` 与
   `\Processor Information(*)\% Processor Performance` **按实例配对**相乘得出，
   跳过一切 `_Total` 汇总实例。普通用户权限，无新依赖，无驱动。
2. **报最快的那颗核**，不报平均。混合架构上平均值会被标称更低或已驻留的能效核压下去，
   与用户机上其它监控工具同一时刻的示数明显不符；「CPU 频率」在这个语境下指的就是最高核频。
3. **超过 10 GHz 的结果整条丢弃**。没有任何在售 x86 处理器接近该值，出现即说明标称值或
   性能比至少有一个是错的——正是 ADR-0028 遇到的 8.18 GHz 那种失效模式——此时报缺失而不是报错值。
4. 该读数由两次采集之间的比值得出，因此与占用率同属**速率类指标**：首拍报 `pending`，
   而不是 `unavailable`。
5. **不为频率画进度条，也不画走势图**。睿频上限在用户态读不到，没有可信的满量程；
   若按窗口峰值相对绘制，则无论机器在做什么都是一条贴顶的近似直线。
6. 温度、风扇两项维持 [ADR-0029](0029-drop-the-bundled-sensor-driver.md) 的结论不变，
   仍恒为 `unavailable`。本次已复核：`MSAcpi_ThermalZoneTemperature` 普通用户仍拒绝访问，
   `Win32_PerfFormattedData_Counters_ThermalZoneInformation` 只有一个恒 0 的芯片组温区，
   `Win32_Fan` 不提供转速。

## 理由

**ADR-0028 错在口径而不是错在结论的严谨度。** 它拿 `_Total` 的标称值和注册表 `~MHz` 去配性能比：
前者是跨核类的平均，后者记录的是开机时刻的频率。两个分子都不属于任何一颗真实的核，
乘出来自然对不上。换成按实例配对之后，分子分母来自同一颗核，结果就是该核的实际平均频率。

**为什么不改口径就等于「一个和任务管理器明显对不上的数字」。** 这条原则本身继续成立，
所以本 ADR 的前提是先给出与第三方工具同量级的实测，再改实现，而不是反过来。

**为什么不取平均。** 本机 14 颗核跨三个核类，标称从 700 MHz 到 1700 MHz。满载瞬间平均 3.77 GHz、
最快核 4.59 GHz，差 0.8 GHz；空载时差距更大。用户是拿本产品替换常驻的第三方监控组件的，
两边同一时刻差出这么多，只会被读成本产品测错了。

## 后果

- 公开 API 层由 8 项读数变为 9 项；不可读项由 4 项减为 3 项（CPU 温度、GPU 温度、风扇）；
- `PdhCounterSet` 多开两个数组计数器，每拍多两次 `PdhGetFormattedCounterArray`，
  规模是核数而非实例数，与既有的 GPU 引擎数组读取同量级；
- 频率的分段文本比百分比长（`CPU 4.59 GHz` 对 `CPU 45%`），但任务栏默认配置不含该项，
  且入口已改为固定槽位（见状态页），长度变化不会推动后续栏；
- [ADR-0029](0029-drop-the-bundled-sensor-driver.md) 第 2 条「不分发任何 WinRing0 系驱动」
  不受影响：本方案完全在用户态。

## 备选方案

- **维持不可读**：与用户明确诉求冲突，且前提（用户态测不准）已被证伪。
- **`CallNtPowerInformation` 或 `Win32_Processor.CurrentClockSpeed`**：本机实测恒等于标称值，
  不随频率变化，等于把标称值当实测值展示。
- **仍按 `_Total` 口径但换一个基准频率**：无论选哪个常数，分母都是跨三类核的平均性能比，
  乘积不描述任何一颗核；本机按 `_Total` 性能比 300% × 1.7 GHz 得 5.11 GHz，已超过该型号睿频上限。
