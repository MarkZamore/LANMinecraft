# Growing and retained G1 heap

The launcher starts at `Xms = min(1024 MiB, Xmx)` and preserves the selected Xmx.
The user's requested policy is to grow on demand and keep the committed heap
capacity until process exit. `-XX:MaxHeapFreeRatio=100` prevents G1's shrink
branch. MinHeapFreeRatio and the existing expansion heuristics stay unchanged;
this does not request the entire Xmx at startup.

The flag is part of both common tuning lists in
`Program/MinecraftProcessService.cs`. The actual launch also passes
`retainCommittedHeap: true` to `CompleteJvmArguments`: after author JVM options,
one effective MaxHeapFreeRatio=100 is inserted before the inherited entrypoint.
Option operands, module/JAR entrypoint pairs, application arguments, Xms/Xmx and
other author flags are preserved. No pack JVM file or personal setting is edited.

This remains a launcher process policy by the user's explicit request, including
custom/offline packs outside the published catalog. Putting it only in each
pack's `launcher/jvm-args.txt` would not apply the requested common behavior to
those existing instances. It is not a content-mod rule or adapter injection.

## Native mechanism

G1 computes a maximum desired heap capacity from MaxHeapFreeRatio. At 100 this
is bounded by Xmx, so committed capacity cannot exceed that shrink threshold.
The minimum desired capacity used for growth is unchanged.

Primary implementations:

- [Java 8 G1 sizing after Full GC](https://github.com/openjdk/jdk8u/blob/master/hotspot/src/share/vm/gc_implementation/g1/g1CollectedHeap.cpp)
- [Java 17 G1 heap sizing](https://github.com/openjdk/jdk17u/blob/master/src/hotspot/share/gc/g1/g1HeapSizingPolicy.cpp)
- [Java 21 G1 heap sizing](https://github.com/openjdk/jdk21u/blob/master/src/hotspot/share/gc/g1/g1HeapSizingPolicy.cpp)
- [Java 25 G1 heap sizing](https://github.com/openjdk/jdk25u/blob/master/src/hotspot/share/gc/g1/g1HeapSizingPolicy.cpp)

Java 8 does not shrink at concurrent Remark. Its Full GC can shrink and must be
tested separately. Later supported runtimes share the ratio-based resizing at
Full GC and concurrent Remark. This policy does not disable collections or
periodic GC, modify the JVM, force Xms=Xmx, or assume that free physical RAM is
the system's available commit.

## Reproduce

```powershell
$env:JAVA_HOME = 'C:\Tools\jdk-25.0.2'
dotnet test Program.Tests -c Debug --filter 'FullyQualifiedName~HeapRetentionTests|FullyQualifiedName~HeapTuningArgumentTests|FullyQualifiedName~FinalJvmArgumentsTests'

# Run the same compiled tests on another supported HotSpot runtime.
$env:G1_HEAP_TEST_JAVA = '<java-home>\bin\java.exe'
$env:G1_HEAP_TEST_OUTPUT_DIR = "$PWD\artifacts\heap-retention\runtime-name"
dotnet test Program.Tests -c Debug --no-build --filter 'FullyQualifiedName~HeapRetentionTests'
```

`Program.Tests/Fixtures/HeapRetentionProbe.java` compiles with `--release 8`.
It allocates and touches two increasing sets of arrays, releases them, and
records committed/used heap and actual GC counters after native collections.
The heap is capped at 512 MiB, starts at 32 MiB, and GC worker counts are capped
only in the fixture. No personal game instance, world, network, or heap dump is
used. Production launcher tuning remains in place, including DisableExplicitGC
for small heaps and ExplicitGCInvokesConcurrent for large ones.

Six runtime cases cover small/large tuning, Full GC/concurrent cycles, and
MaxHeapFreeRatio=70 negative controls. Native `GC.class_histogram` requests Full
GC without changing the production flags; its returned histogram is discarded.
`GC.run` exercises the concurrent large-heap path. Each case finishes with a Full
GC so Java 8's negative control exercises its actual shrink site. A passing
retention case must grow, collect released objects, increase GC counters, and
have no committed-capacity decrease. A passing control must actually shrink.

Additional cases test real CML composition, author-option precedence without
file changes, option operands/module boundaries, and the resulting native Java
process with and without pack options.

## Scope and cost

Retained heap capacity continues consuming system commit until the process
exits, even when the live objects occupy much less. Windows may still page out
physical pages. The one-shot memory admission/warning policy from release 436
remains active, and it does not reserve future memory.

This removes shrink-followed-by-regrowth, not pauses from initial expansion,
garbage collection, page faults, mod work, or world loading. Synthetic results
do not establish Minecraft frame times or promise a freeze-free game.
