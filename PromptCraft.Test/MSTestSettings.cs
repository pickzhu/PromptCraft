// OpenAiHttpHelper 的 SenderOverride 是静态共享测试钩子，必须串行执行测试，
// 否则并行用例会互相覆盖 mock 导致误报。
[assembly: DoNotParallelize]
