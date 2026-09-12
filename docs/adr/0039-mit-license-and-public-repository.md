# ADR-0039：项目采用 MIT 许可证并公开仓库

状态：Accepted
日期：2026-09-12
关系：落定 [ADR-0006](0006-source-reuse-and-license.md) 留给用户的「项目许可证由用户后续决定」一条；
该 ADR 的源码复用禁令与依赖审查义务继续有效，不被本 ADR 取代

## 背景

ADR-0006 在 2026-08-06 把许可证决定挂起，并据此约束整个开发期：不复制参考项目源码、
不使用来源项目资源、逐项审查依赖、不让 GPL/AGPL/Anti-996 代码进入代码库。该约束已经执行到现在，
代码库中没有第三方源码，也没有图标、字体或示例资源（`git ls-files` 中无任何二进制资源文件）。

许可证挂起的代价现在开始显现。仓库仍为私有，README 结尾写着「项目许可证尚未决定」，
这对外意味着：无人可以合法使用、修改或分发本项目，也无人能判断是否值得提交 PR。
产品原则 7（[01-product-requirements.md](../01-product-requirements.md) §2）已经把
「作为开源工具，参考机只是验证环境」写成设计约束——代码按公开发布写，仓库却不公开发布。

## 决策

1. 项目采用 **MIT 许可证**，`LICENSE` 置于仓库根目录，版权行为 `Copyright (c) 2026 CookPiu`。
2. 仓库切换为 public。
3. ADR-0006 的以下条款**不因许可证落定而失效**：不复制第三方源码与资源；新增依赖必须在
   [development/dependencies.md](../development/dependencies.md) 记录用途、版本、许可证与分发影响；
   GPL、AGPL、Anti-996 及带额外限制的代码不得进入本代码库。
4. 第三方 Notice 与 SBOM 仍是**分发二进制**的前置项，不是公开源码的前置项。公开仓库只分发源码，
   源码不包含第三方文件；首个发布二进制之前按 ADR-0006「后续」清单补齐。
5. 依赖策略与 MIT 相容：当前直接依赖只有 Microsoft 官方 Windows App SDK / Windows SDK BuildTools
   （Microsoft 软件许可条款，仅构建与自包含运行时）与测试期的 MSTest / Test.Sdk（MIT），
   无一施加 copyleft 义务。

## 理由

选 MIT 而非 Apache-2.0 或 GPL：

- MIT 与既有依赖策略同向。ADR-0006 与 dependencies.md §5 已经明确拒绝 copyleft 代码进入本库，
  自身再采用 GPL 会造成「禁止吸收 GPL、却要求下游遵守 GPL」的方向冲突，且需要改写那条既有规则。
- 本项目是单人维护的 Windows 桌面工具，没有专利组合需要防御，Apache-2.0 的专利授权与
  NOTICE 传递义务带来的文本负担换不到对应收益。
- MIT 对采用率最友好，也最容易被下游的合规流程无争议地通过。

## 后果

正面：

- 用户与贡献者获得明确、无争议的使用与分发授权；
- 可以接收外部 PR——在没有许可证时，外部贡献的授权状态本身是不清晰的；
- 允许发布 GitHub Release 二进制。

负面：

- MIT 不阻止闭源再分发，也不阻止商业方直接取用；这是选择宽松许可证的已知代价，接受；
- 公开后 224 条提交历史、全部文档与 ADR 立即对外可见，且实际上不可撤回（可能已被镜像或索引）。
  发布前已确认历史中无凭据、无个人路径，提交作者统一使用 GitHub noreply 邮箱；
- 公开仓库的 issue 与 PR 带来维护负担，由 `CONTRIBUTING.md` 与 `SECURITY.md` 界定响应边界。

## 后续

- 首个二进制 Release 之前：生成传递依赖清单与第三方 Notice，复核 WorkspacePanel 发布目录中的
  Windows App SDK 可分发文件范围（ADR-0006「后续」）；
- 发布产物当前不做代码签名，用户首次运行会遇到 SmartScreen 提示，须在 README 中如实说明；
- 新增依赖继续按 dependencies.md §5 逐项记录，并额外确认与 MIT 相容。
