# Test consolidation map (phase 3 input)

Per-method audit of the 26 event-named test files (`AuditDefect*`, `CorrectnessFixes*`,
`*Followups*`, `IngestionCouncilFixesTest`, `McpFollowupsEndpointTest`): 209 methods read
against every candidate subject file, grep-verified. Verdicts: **delete** (verified pure
duplicate, evidence quoted at audit time), **relocate** (unique content, natural subject
home exists), **keep** (unique, no better home; the file may still be renamed to a
subject name). Methods not listed below are keeps.

## Verified pure duplicates - delete (9)

| Method | Duplicates |
|---|---|
| `AuditDefectAnalyticsReachTest.BuiltInAlgorithm_IsUnchanged_WithAnEmptyRegistry` | `AnalyticsEndpointTest.Algorithms_ListsTheFiveBuiltins` + `Run_TopK_OrderingAndCeiling_WithStatistics` |
| `AuditDefectNamespacePatchTest.Patch_RenameWithInvalidOverride_Rejects_AndLeavesTheNameUntouched` | `NamespaceEndpointTest.Patch_LoadOnStartup_RoundTripsTheTriState_AndRejectsAnythingElse` (its "(B31's ordering)" block) |
| `AuditDefectNamespacePatchTest.Patch_RenameWithValidOverride_AppliesBoth` | same subject method, "one atomic update" block |
| `CorrectnessFixesTest.DictionaryIndex_WhenAddingMultipleValuesUnderOneKey_ShouldReturnAllOfThem` | `IndexTest.DictionaryIndex_BasicOperations_ShouldWorkCorrectly` |
| `CorrectnessFixesTest.DictionaryIndex_WhenRemovingOneValueFromAKey_ShouldKeepTheRest` | same subject method |
| `CorrectnessFixesTest.GetPropertyCount_OnElementCreatedWithoutProperties_ShouldReturnZero` | `PropertyStoreFidelityTest.NullAndEmptyPropertySets_ReportAsEmpty_AndAllocateNoContainer` |
| `CorrectnessFixesFollowupsTest.ProcessTransaction_WhenTryExecuteThrows_RecordsThrownExceptionAndRollsBack` | `TransactionFailureReasonTest.ProcessTransaction_ThrownException_IsClassifiedAsInternalError` |
| `CorrectnessFixesFollowupsTest.ProcessTransaction_WhenTryExecuteReturnsFalseCleanly_RollsBackWithNullError` | `TransactionFailureReasonTest.RemoveSubGraph_Missing_RollsBackWithNotFound` |
| `CorrectnessFixesFollowupsTest.SaveAndLoad_WithSpatialIndexPresent_SpatialIndexSurvivesAndIsQueryable` | `PersistenceEncodingTest.Spatial_PointAndMbrEntries_RoundTripAndAreQueryableOnReload` |

Also claimed as pure duplicates outside these 26 files (from the cross-layer and config
audits): `SubGraphControllerTest.Create_DoesNotMutateSourceGraph`,
`PropertyMutationEndpointTest.PutProperties_SetsAndRemoves_InOneBatch`,
`NamespaceDurabilityTest.AddressingANotLoadedNamespace_Answers503_NotFound404` (strict
subset of `NamespaceEndpointTest.DataRoute_OnANotLoadedNamespace_...`).

**CORRECTION, made at implementation time: one of those three was WRONG and was NOT
deleted.** `PropertyMutationEndpointTest.PutProperties_SetsAndRemoves_InOneBatch` is not a
duplicate. Both halves of the audit's evidence fail against the code: no sibling in that
file asserts `Accepted` for `PUT /graphelements/properties` (every other 202 assertion is on
a different route, and the only other test on this route asserts `BadRequest`), so deleting
it would have left the batch route with NO success-path coverage at all. The engine-level
`PropertyReplaceTest.SetProperties_SetsAndRemoves_InOneAtomicBatch` does not reach the route.
Verified deletes are therefore **11**, not 12. Recorded here because this map is the thing a
later reader would trust.

## Relocation targets (85 methods; whole-file moves where all rows agree)

| Source file | Methods | Target(s) |
|---|---|---|
| `AuditDefectSubGraphRestTest` | 18/18 | algorithm-selector group to `SubGraphControllerTest`; recalculate-quota group to `SubGraphQuotaTest` |
| `AuditDefectSubGraphTest` | 15/15 | leading-edge-pattern group to `SubGraphTest`; nested-recalc rebinding group to `SubGraphNestedTest` |
| `AuditDefectCodegenTest` | 10/10 | B24 group to `PathFilterArityTest`; B09 cap group to `SubGraphCodeGenerationTest` |
| `AuditDefectPersistenceTest` | 8/8 | `AdminControllerContractTest` (currently one method; becomes the AdminController contract home) |
| `AuditDefectBoundIndexTest` | 6/6 | `VectorIndexEndpointTest` (which has zero DELETE-route coverage today) |
| `AuditDefectNamespacePatchTest` | 5/7 | `NamespaceEndpointTest` (2 deleted above; keep only the genuinely new halves, trim the overlapping assertions) |
| `AuditDefectReportingTest` | 3/8 | `/statistics` group to `ObservabilityEndpointTest`; the Manufacturer-virtual group keeps |
| `AuditDefectLimitsTest` | 3/7 | dead-knob rows to `SettingCatalogTest` (two verbatim assertion lines already exist there - trim on merge); benchmark rows to `BenchmarkEndpointTest` |
| `AuditDefectMessagesTest` | 3/8 | B13 rows to `BulkImportExportTest`, B33 rows to `ChangeFeedEngineTest`; trim the halves those files already assert verbatim |
| `AuditDefectAnalyticsReachTest` | 1/9 | `UnknownAlgorithm_Still404ProblemJson_OnBothEndpoints` to `AnalyticsEndpointTest`; the plugin-reach methods keep (no subject file combines runtime plugins with AnalyticsController) |
| `CorrectnessFixesTest` | 5/13 | RegEx/Range multi-value rows to `IndexTest`; `TransactionWorker_WhenATransactionThrows_...` to `TransactionFailureReasonTest` |
| `CorrectnessFixesFollowupsTest` | 3/12 | 404-wait row to `TransactionFailureReasonTest`; success-wait row to `GraphControllerTest`; edge-detach rollback beside its vertex sibling |
| `EnginePerformanceFollowupsBenchmark` | 1/2 | `P4_OrderedIndexScan_RangeVsGeneric_ScalesWithK` merges into `EnginePerformanceBenchmark.P4_RangeQuery_ScalesWithLogNPlusK` |
| `IngestionCouncilFixesTest` | 2/7 | RegEx tombstone guard to `IndexIntegrityTest`; docling-off status row to `IngestionEndpointTest` (supersedes its weaker sibling) |

## Keeps with no better home (115) - rename the file, keep the content

Whole files that stay intact under a subject name: `AuditDefectCreateTypeGuardTest`
(14, the only create-time property-type-guard coverage), `AuditDefectPathBudgetTest`
(12, the only budget coverage), `AuditDefectNeighboursTest` (8),
`AuditDefectModificationDateTest` (8), `AuditDefectMcpAlgorithmTest` (8),
`AuditDefectOpenApiDocumentTest` (6), `AuditDefectDocumentContractTest` (6),
`AuditDefectPublishedSamplesTest` (5), `AuditDefectDtoMetadataTest` (4),
`AuditDefectRemarksTest` (2), `EnginePerformanceFollowupsTest` (4, disclaimed as
non-duplicate by `ScanResultRepresentationTest` itself), `McpFollowupsEndpointTest` (3).

## Warnings that gate the fold

1. **`AuditDefectMcpAlgorithmTest` is the ONLY file in the suite exercising `PathsTool`
   and `SubgraphTool`.** Its event name undersells it; rename, never thin.
2. **`IndexTest`'s own `Between` assertions are written around bug B3**, tolerant by
   their own comment ("we'll adjust our assertions to match the actual behavior"). When
   the corrective `RangeIndex_Between_*` tests move in, the defensive assertions are
   replaced by them, not kept alongside.
3. `CorrectnessFixesFollowupsTest`'s `ThrowingOnSaveIndex`/`ThrowingOnLoadIndex` fixtures
   are referenced by `AuditDefectPersistenceTest`, `AuditDefectReportingTest`, and
   `EnginePerformanceTest`: move them to a shared fixture home before any file moves.
4. Line-level trims inside methods that stay: the verbatim-duplicated assertion lines
   between `SettingCatalogTest`/`AuditDefectLimitsTest` and between
   `BulkImportExportTest`+`ChangeFeedEngineTest`/`AuditDefectMessagesTest`.
