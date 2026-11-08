# Forge handshake concurrency probe

`Program.Tests/Fixtures/ForgeHandshake/ForgeHandshakeProbe.java` runs the bytecode
guard regressions and a deterministic ACK/add race. Compile it with the adapter
sources and the pinned ASM core/tree jars, using `javac --release 17`. The class is
in the default package. Normal execution needs no Minecraft installation.

Pass the exact verified Forge 1.20.1 / 47.4.0 universal JAR as its sole optional
argument to run the same test against the native class. Its expected SHA256 is
`2b0e97b22fd4d043dbbcc8e0a44ef0d58915689ed9224e4cd28d5c4976a16bcc`.

The native probe projects the original constructor's `sentMessages` initializer,
the native list-operation instruction ranges (`removeIf`, `add`, `isEmpty`,
`contains`) and the original invokedynamic ACK predicate into an isolated class.
Only the holder owner/local receiver are relocated; Forge logging, world loading
and unrelated handshake lifecycle code are outside the execution boundary.

The ACK predicate is paused with latches while the server-side operation adds a
new pending index. The original native ArrayList must throw CME. With the single
reviewed initializer wrapper, add must wait for removeIf and the new unacknowledged
index must remain pending. Further checks cover unknown ACKs, callback exception
propagation and independent locks for separate connections.

This is execution of isolated native bytecode and structural comparison of the
complete original/transformed class. It is **not** a real two-client connection,
full game run, authentication test or proof that every disconnect is fixed.
Keep temporary outputs under the owning task's `artifacts/agent-runs` directory.
