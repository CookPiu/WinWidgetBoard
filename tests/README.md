# 测试目录规划

预计结构：

```text
tests/
├─ Unit/
├─ Contract/
├─ Integration/
├─ UiAutomation/
├─ Performance/
├─ Security/
└─ Manual/
```

测试要求详见 `docs/08-testing-strategy.md`。任何任务栏定位、点击穿透、拖拽编排或动效变更都必须包含真实 UI 验证，不能只运行单元测试。
