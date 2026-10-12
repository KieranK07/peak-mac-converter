# Open questions on SPEC.md (from the clean-room implementer)

Each one has the choice made in the meantime. None changes the A2 trace.

1. **§2 `.bundle` format.** "A Mach-O bundle named `MemoryWrapper-x64-Release.bundle` (the format Photon and Steamworks.NET use)": is that a directory CFBundle or one flat Mach-O file with a `.bundle` name? **Chosen:** a directory CFBundle (`Contents/Info.plist`, plus `Contents/MacOS/MemoryWrapper-x64-Release` as an `MH_BUNDLE` binary). `build.sh` always builds it, but the `.dylib` is what ships unless A3 says otherwise.
2. **§8 labelled abort scope.** The spec names only "the first allocation". **Chosen:** all three callbacks check the stored manager and do the one-line-then-`abort()`, because on Windows any of the three would dereference the missing interface. The only call that can come first is an allocate, so the result at the §9 point is the same.
3. **§10 S1 reallocate `P`.** It doesn't say which block `P` is. **Chosen:** a chain, where each reallocate takes the block the previous one returned, and "release of P" frees the last one. The 36 blocks from the allocate grid are freed by the harness straight through the fake (not traced), so the trace has only the `Deallocate` calls the spec lists.
4. **§10 S2 child result.** The exit status depends on the OS (Mac: SIGABRT, wait status 6; Windows: 0xC0000005). **Chosen:** the trace only says `process terminated` (any non-zero status). The raw status goes to stderr.
5. **§6 second load where `GetInterface` returns null.** Step 5 says "overwrite both stored values", but step 3 says make no further calls. **Chosen:** store the null manager and leave the allocator as it was. This can't be observed: unload needs both values non-null, and the callbacks abort while the manager is null.
