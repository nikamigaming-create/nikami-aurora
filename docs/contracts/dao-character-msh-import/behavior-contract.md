# Dragon Age: Origins character MSH import contract

## Boundary

This contract covers only the skinned geometry stored in exact installed PC
`GFF V4.0 / MESH V0.1` resources selected by the 24 source-bound character-
creation MOP presets. `DragonAgeOriginsMshDecoder` is profile-owned and consumes
legally owned bytes in memory. It does not write retail payloads, select an
outfit, resolve MAO/MAT/DDS data, map the MMH bone palette, choose a standing or
bed pose, or infer morph correspondence from resource names.

The accepted installed container is
`packages/core/data/modelmeshdata.erf`, SHA-256
`3011a2c2f5e9142639d58ee7624dcda7fb4f3c3bc538237c4c87e48e9b2d97fc`.
Payload hashes remain attached to every decoded MSH definition; neither the
container nor its extracted members are repository artifacts.

## Exact decoded subset

The decoder requires the observed five-structure MESH schema (`mesh`, `decl`,
`bnds`, `strm`, `chnk`) including exact field types, flags, offsets, and sizes.
Unknown versions, schemas, root/chunk control values, external streams, vertex
semantics, declaration types, non-triangle index counts, invalid references,
non-finite attributes, negative palette indices, empty weights, invalid bounds,
and out-of-range indices fail closed.

Each named chunk preserves source bounds, vertex/index buffer offsets, the
numeric vertex declaration, positions, normals, tangent frame, UV0, optional
vertex color, four half-float source weights, four signed-short palette slots,
and unsigned triangle indices. Declaration type and usage values use the
documented Direct3D 9 identities: [D3DDECLTYPE](https://learn.microsoft.com/en-us/windows/win32/direct3d9/d3ddecltype)
and [D3DDECLUSAGE](https://learn.microsoft.com/en-us/windows/win32/direct3d9/d3ddeclusage).
The MSH decoder reports the installed coordinate basis explicitly as
right-handed, Z-up. Conversion to Aurora's right-handed, Y-up basis remains the
adapter's responsibility through the existing
`DragonAgeOriginsCoordinateSystem` contract.

Normals and tangent XYZ are normalized for the downstream glTF contract;
tangent W is the sign of the authored normal/tangent/binormal frame. Thirteen
referenced vertices in four installed resources contain an authored zero
tangent/binormal. Each has an exact duplicate source vertex with the same
position, normal, and UV and a nonzero frame, so the decoder reuses only that
source frame and reports `ReconstructedTangentVertices`. It does not generate a
name-based or arbitrary tangent.

The public handoff is decoder-specific: `DragonAgeMshDefinition`,
`DragonAgeMshSubmesh`, `DragonAgeMshBounds`,
`DragonAgeMshVertexDeclaration`, `DragonAgeMshSkinInfluence`, and
`DragonAgeMshMorphTarget`. Palette indices are not called skeleton joint
indices until independently joined through the MMH bone-index list. Materials
and attachment nodes likewise remain MMH/MAO adapter inputs.

## Installed dependency gate

`dao-character-msh-audit` first obtains the exact dependencies from the MOP/MMH
audit, then hashes and decodes each MSH directly from the owned archive. The
current corpus result is:

```text
selections=24 mesh_dependencies=227 meshes_decoded=227 meshes_failed=0
submeshes=251 vertices=390022 indices=1763625
coordinate_basis=source-right-handed-z-up reconstructed_tangent_vertices=13
```

The 251 chunks use four declaration signatures. All use a single interleaved
stream with POSITION, UV0, TANGENT, BINORMAL, NORMAL, BLENDWEIGHT, and
BLENDINDICES; seven chunks additionally contain COLOR. Position is either
FLOAT4 or FLOAT16_4, normal is FLOAT3 or FLOAT16_4, UV0 is FLOAT16_2, tangent/
binormal/weights are FLOAT16_4, and palette slots are SHORT4. The maximum
source half-float weight-sum error and maximum palette slot remain explicit in
per-resource audit entries.

## Morph boundary and blocker

`BuildMorphTarget` can produce dense position/normal/tangent deltas only when a
caller supplies a base and target chunk with identical vertex count and exact
index sequence. Synthetic source-free fixtures prove the positive join and the
incompatible-topology rejection.

Across the installed 24-MOP census, all 650 modifier placements have no exact
base-submesh vertex/index correspondence. A representative universal head base
has 2,208 vertices while a nose modifier has 2,214, and 10,437 of 10,788 index
positions differ. The decoded MESH schema contains no vertex remap or
barycentric correspondence table. The joined MMH `xprt` and `crst` contracts
also expose no vertex IDs, remap, barycentric coordinates, or triangle locator.
The next exact blocker is therefore
`source-morph-crust-correspondence-contract-unavailable`. Modifier weights and
resource hashes are preserved, but no dense morph target is fabricated and no
fresh all-24 character claim is made from legacy GLBs.

## Acceptance

Source-free acceptance covers identity/hash/basis, schema and declaration
decode, bounds, triangle indices, palette slots, raw and normalized weights,
tangent handedness, exact-topology morph deltas, and rejection of unknown
versions/semantics, bad indices, and incompatible morph topology. The installed
audit is metadata only and emits no extracted meshes, raw buffers, caches, or
converted retail assets.
