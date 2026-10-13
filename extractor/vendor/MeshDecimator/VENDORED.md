# MeshDecimator (vendored)

- Upstream: https://github.com/Whinarn/MeshDecimator
- Commit: `a1e85d43c0bea241f1952e7c093f83bb8d3ad1d8` (2020-08-28)
- License: MIT, Copyright (c) 2017-2018 Mattias Edlund (`LICENSE.md`, unchanged). The algorithm is a port of
  Sven Forstmann's Fast-Quadric-Mesh-Simplification (MIT, notice kept in `src/Algorithms/FastQuadricMeshSimplification.cs`).
- Compatible with the extractor's GPL-3.0-only licence (MIT is GPL-compatible); ship `LICENSE.md` with the binary.

## What was taken

`MeshDecimator/` library sources only, copied byte-for-byte into `src/`:
`Algorithms/`, `Collections/`, `Loggers/`, `Math/`, `BoneWeight.cs`, `Logging.cs`, `Mesh.cs`, `MeshDecimation.cs`.

## What was stripped

- `MeshDecimator/Properties/AssemblyInfo.cs` and the old-style `MeshDecimator.csproj` (replaced by an SDK-style
  `MeshDecimator.csproj` targeting the extractor's framework, nullable and implicit usings off).
- `MeshDecimatorTool/` (OBJ command-line tool), `UnityExample/`, solution file, README/CHANGELOG.

## Patches

None. The source files are unmodified; all extractor-specific handling (scale normalisation, per-node budgets,
re-normalising normals, validation and the vertex-clustering fallback) lives in
`extractor/src/CthExtract.Core/Decimate.cs`.

## Verification

- Builds as net10.0 with no warnings (`dotnet build vendor/MeshDecimator`).
- `extractor/tests/CthExtract.Cli.Tests/DecimateTests.cs` (synthetic meshes made by the test code): a 30,000-triangle
  two-material UV sphere goes under 8,000 / 3,000 / 1,000 budgets in about 150 ms each with materials, node pivots and
  finite output kept. Volume error 0.14 % / 0.36 % / 0.88 % against 1.5 % / 3.7 % / 7.6 % for the vertex-clustering
  fallback (which before this change could not reach 3,000 on that mesh at all and truncated it: 60 % error).
- Known limit, handled in Decimate.cs: edge collapse cannot remove zero-area triangles (a UV sphere's pole rows stall
  it near 2,150 triangles) or shrink many small closed parts below a few triangles each. Zero-area triangles are
  dropped first; if the quadric pass still misses the budget after three passes, vertex clustering takes over.
