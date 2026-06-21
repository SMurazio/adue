using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mmo.Server.Tests")]
// NET2: TEST1 (TimingFaithfulReconcileHarnessTests, in Mmo.Client.Core.Tests) drives the REAL
// GameServer.ExtractFreshStepCommits to assert UO-commit-loss recovery against the production extractor.
[assembly: InternalsVisibleTo("Mmo.Client.Core.Tests")]
