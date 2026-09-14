# Manimal Interchange Rework Backport

Backport implementation in progress for replacing SPT 4.1.5's Interchange with the installed retail EFT 1.1.5.47242 map.

- [Complete backport plan](docs/INTERCHANGE-BACKPORT-PLAN.md)
- [Implementation status and reproduction](docs/IMPLEMENTATION-STATUS.md)
- [Full-map sky, cable, and outdoor audio corrections](docs/VISUAL-AUDIO-REPAIRS.md)
- [Recovered retail assemblies and source layouts](docs/RETAIL-ASSEMBLY-RECOVERY.md)
- [Verified SPT AI Bridge loader tests](verification/loader-probe.json)
- [Verified native texture import and SPT AI Bridge tests](verification/native-texture-probe.json)
- [Verified shared-asset donor loading through SPT AI Bridge](verification/native-reuse-probe.json)
- [Verified rotated-room geometry through SPT AI Bridge](verification/room-geometry-probe.json)
- [Verified area-light initialization and active scene lifecycle](verification/area-light-probe.json)
- [Verified seasonal terrain and mesh selection](verification/season-probe.json)
- [Verified ballistic hit policies and native explosions](verification/ballistic-trigger-probe.json)
- [Verified retail cloud shader, GPU rendering and cleanup](verification/cloud-probe.json)
- [Preserved AI mine-point and core links](verification/ai-point-links.json)
- [Target conversion progress](verification/conversion-progress.json)
- [Measured findings and comparison tables](docs/AUDIT-FINDINGS.md)
- [Audit methodology and reproduction](analysis/README.md)
- [Scene comparison](analysis/output/scene_diff.csv)
- [Component comparison](analysis/output/component_diff.csv)
- [Asset comparison](analysis/output/asset_diff.csv)
- [Pinned upstream server-data comparison](analysis/output/upstream_comparison.json)
- [Actual SPT 4.1.5 model validation](analysis/output/upstream-model-verification.json)
- [Audit reconciliation and source verification](analysis/output/verification.json)

The source snapshot, exact-target authoring SDK, paired development plugins and an 18-scene loader probe are implemented. Decrypted retail metadata provides 146 analysis assemblies and complete structural recovery of all 232,278 scene components and 989 referenced script assets. Current conversion counts, explicit omissions and unresolved references are recorded in [conversion progress](verification/conversion-progress.json). These count prepared map records; they do not count rewritten game scripts or completed runtime tests. The full replacement map now loads in the development install; broader gameplay validation remains ongoing.

SPT AI Bridge loader tests cover target script binding, repeat loading, cancellation and cleanup. Two native-texture tests verify loading, native graphics allocation, readable byte hashes and cleanup for 16 assets representing 26 source identities. Two shared-asset tests verify all 302 native donor candidates, including file hashes, graphics buffers, audio data loading and identical native instances on repeat. Two room-geometry tests verify the actual patched target method, imported component, oriented boundaries, multiple areas, cleanup and zero warmed allocations. Two area-light tests verify all 1,620 authored brightness/color/shape settings and three active bundled lights through Awake/OnEnable and natural scene cleanup. These tests do not establish a playable full map.

The full-map development package is installed in `D:\SPT41Dev` with a rollback inventory under `build/deployments`. With its development gate enabled, Interchange raids load the replacement map; the user and SPT AI Bridge have confirmed it loads. Visual and ambient-audio regression work is tracked in the correction report above. No core level/database files or reference projects were changed. There is no publication-ready release package yet. Root `Directory.Build.props` defines the mod identity; its source URL remains empty because this project's source repository has not been supplied or published.
