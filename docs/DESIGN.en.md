[简体中文](DESIGN.md) · **English**

# Design

This document explains *why* UnlockKit does each thing the way it does. Every
mechanism below exists because a simpler one failed, usually in a way that
crashed a machine or produced a wrong answer.

## 1. The shape of the problem

"I can't delete this file" has exactly five causes on Windows:

| Cause | Detectable from user mode? | Fixable from user mode? |
| --- | --- | --- |
| A process holds a handle without `FILE_SHARE_DELETE` | Yes — enumerate handles | Yes — close the handle |
| Nothing holds it, the ACL lacks `DELETE` | Yes — try to open with `DELETE` | Yes — change the ACL (not done by default) |
| A filter driver holds the object | No — only its absence is visible | **No** |
| The file is a mapped image section (running `.exe`/`.dll`) | Yes — `STATUS_CANNOT_DELETE` | **No** |
| The holder is a PPL-protected process | Yes — query protection level | **No** |

A tool that returns `true`/`false` collapses five cases into one bit and is
therefore wrong about four of them. UnlockKit computes the cause first and
selects a response from it. That is the entire architecture: **diagnose → act**,
never act → retry blindly.

## 2. Finding the holder

### Enumerate every handle in the system

`NtQuerySystemInformation(SystemExtendedHandleInformation, …)` returns every
handle in every process, with the owning PID and the granted access mask. This is
the only complete view: per-process enumeration misses anything that duplicated a
handle into a child, and `Restart Manager` only covers well-behaved applications
that registered themselves.

The buffer size is not documented and races with system activity, so the call is
retried with a growing buffer on `STATUS_INFO_LENGTH_MISMATCH`.

### The type index is discovered, not assumed

Every entry carries an `ObjectTypeIndex`, and "File" happens to be `25` on most
current builds — which is exactly why hardcoding it is a bad idea. Type indices
are assigned at boot and are not contractual. Instead of trusting a constant, the
scanner creates a temporary file, opens it, asks the handle table which entry
belongs to *its own* PID and handle value, and reads the index from there. If that
probe fails, the scan reports "type index unknown" rather than silently matching
zero handles and claiming the file is free.

### Filter by object type before touching anything

The `File` object type also covers named pipes, mail slots, and character
devices. `GetFileInformationByHandle` **blocks forever** on a synchronous pipe
handle — the second the scanner touches one, the tool hangs with no diagnostic.

So: `GetFileType(handle) == FILE_TYPE_DISK` (`1`) is checked first, and anything
else is skipped. This is not a performance filter, it is the difference between
working and deadlocking. It also structurally excludes section objects, ALPC
ports, and desktop objects, which is why the scanner cannot accidentally close
something that would bugcheck the machine.

### Match by identity, not by name

Resolving every handle's object name with `NtQueryObject(ObjectNameInformation)`
is the obvious approach and it is a trap: on some filter drivers it deadlocks,
and on all of them it is slow (thousands of round-trips through the object
manager for a single scan).

Instead each candidate handle is duplicated into this process and asked for its
**volume serial number + 64-bit file index** via `GetFileInformationByHandle`.
That pair is the identity of the file on that volume:

- it does not care how the file was opened (`\\?\` prefix, 8.3 name, hardlink,
  junction, different letter for the same volume);
- it cannot be spoofed by a path that merely *looks* like the target;
- it costs one syscall per handle instead of an object-manager walk.

Only when the identity does not match, but the *volume* does, and the target is a
directory, is the path resolved (`GetFinalPathNameByHandle`) and tested for
containment. Network handles are never queried, because the volume filter rejects
them first — which is what keeps a subtree scan from stalling on an offline SMB
share.

### Subtree scanning

Deleting `D:\archive\ccccc` fails when nothing holds the folder but something
holds `D:\archive\ccccc\assets\config.json`. The folder cannot be removed while a
child exists, and the child cannot be removed while it is held.

So a directory target switches the scanner into **subtree mode**: every handle on
the target's volume whose resolved path is prefixed by the target path is a
match. This is what turns "no locks found, refuses to delete" into
"`TSVNCache.exe` holds 5 directories inside this folder".

## 3. Closing a handle in another process

`DuplicateHandle(sourceProcess, sourceHandle, …, DUPLICATE_CLOSE_SOURCE)` closes
the source while handing you a copy. It is the only supported way to close
another process's handle, and it is unforgiving: handle *values* are recycled
immediately, so a value captured during enumeration may already belong to an
unrelated object by the time you act on it.

The scanner therefore does it in two steps for every single handle:

1. `DuplicateHandle` **without** `DUPLICATE_CLOSE_SOURCE` → re-run the identity
   predicate against the copy → close the copy. If the identity no longer holds,
   the handle was recycled; skip it and count it as *unverified*.
2. Only after the identity is confirmed, `DuplicateHandle` **with**
   `DUPLICATE_CLOSE_SOURCE`.

There is still a gap of microseconds between step 1 and step 2, and there is no
API to close a remote handle conditionally on the object it points at. That gap
is documented rather than hidden, and it is why the counter of *unverified*
handles exists in the report: it is the honest measure of how much the scanner
could not prove.

Handles capable of writing (`FILE_WRITE_DATA`, `FILE_APPEND_DATA`) produce a
warning before being closed, because closing them can lose data the holder never
flushed.

## 4. The delete ladder

Order is the design. Cheapest and least destructive first, and each rung reports
itself so the caller can say *why* the fallback happened.

```
1. POSIX delete          NtSetInformationFile(FileDispositionInformationEx)
                         with FILE_DISPOSITION_POSIX_SEMANTICS
                         → unlinks the name now; bytes freed when the last
                           handle closes. Works even while others hold it open,
                           as long as they allowed FILE_SHARE_DELETE.
2. Close, then delete    only reached on STATUS_SHARING_VIOLATION, i.e. a holder
                         that did not allow share-delete.
3. Recursive tree delete directories only: close the whole subtree in one pass,
                         then delete files first and directories post-order.
4. Reboot schedule       MoveFileEx(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)
                         → PendingFileRenameOperations, executed by SMSS before
                           any user-mode code runs. Opt-in only.
```

Notes on the rungs that matter:

- **`FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE`** is set in the same call, so a
  read-only flag does not need a separate `SetFileAttributes` round-trip that
  could itself fail.
- **Rung 3 exists because of watchers.** `TSVNCache.exe`, Search indexers, and
  cloud sync clients re-open directory handles within milliseconds of losing
  them. Deleting file-by-file with retries loses that race forever; closing the
  entire subtree once and then deleting bottom-up finishes in ~1.7 s on a
  five-directory tree that had been "undeletable" for weeks.
- **Rung 4 is never automatic** in the CLI; it needs `--reboot`. Scheduling a
  delete that the user did not ask for is a surprising side effect, and it
  survives the session that requested it.
- **Junctions and symlinks are deleted as links**, never recursed into. A
  directory junction loop otherwise walks until the stack dies.

## 5. Safety policy

Closing a handle in a process that does not expect it can range from harmless to
a bugcheck. Rather than judge case by case, a fixed deny-list is applied by
process name, plus PID 0/4:

`System`, `Idle`, `Registry`, `smss`, `csrss`, `wininit`, `winlogon`, `services`,
`lsass`, `lsaiso`, `MemCompression`, `MsMpEng`

PPL-protected processes are refused as well — not out of politeness, but because
`OpenProcess` cannot succeed against them even with `SeDebugPrivilege`, so
attempting it produces a misleading "access denied" instead of the true reason.

The policy is deliberately not configurable. A flag to override it would be used,
and the failure mode is a machine that no longer boots.

## 6. Elevation and privileges

`SeDebugPrivilege` is enabled at startup (`AdjustTokenPrivileges` on the current
process token); without it, handles in processes owned by other users are
unreadable and the report says so via the *unreadable* counter rather than
silently returning "not locked".

Elevation is not demanded up front. Most single-user cases (a watcher in your own
session) work unelevated; the window offers *Restart as administrator* when the
scan shows handles it could not open.

## 7. Shell integration

One `HKCU` parent verb per scope (`*` and `Directory`), pointing at a cascade
definition through `ExtendedSubCommandsKey`. The parent carries `MUIVerb` and
`Icon`, and deliberately has **no** `SubCommands` value and **no** `command`
subkey — either one would replace the cascade with a plain verb or an explicit
list.

Children (`Unlock`, `Delete`) are read from the definition key's `shell` subkey.
Their captions come from `MUIVerb`; the default value is written too so the same
key remains correct if a host reads it as a plain verb. Each child gets its own
`.ico` extracted next to the executable, because 16-pixel menu icons are the only
thing distinguishing two similar verbs.

Everything is per-user. No elevation, no `HKLM`, no uninstaller required: the
window's *Remove context menu* deletes the same keys it created, plus the flat
verb names used by earlier builds.

## 8. Themes

Light and dark are not two windows; they are two colour dictionaries. Every
style lives in `Themes/Controls.xaml` and every colour reference in it is a
`DynamicResource`; `Themes/Light.xaml` and `Dark.xaml` each declare the same set
of keys and nothing else. Switching replaces the single entry at
`Application.Resources.MergedDictionaries[0]` — no control is rebuilt, so scroll
position, row selection and a log that is still being appended all survive.

There is one trap: merged dictionaries resolve **last to first**, so a newly
inserted dictionary at index 0 would lose to the light dictionary still sitting
at index 1. The entry has to be replaced in place, not added.

Two things styles cannot reach:

- **The title bar belongs to DWM, not WPF.** A dark client area with a white
  frame looks broken. `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`
  tells DWM; the attribute number moved from 19 to 20 around Windows 10 20H1, so
  both are attempted.
- **Scroll bars.** The stock WPF scroll bar is a light grey slider that reads as
  a foreign object in a dark window. Both themes share one 11-pixel, arrow-less
  scroll bar driven by the same colour keys.

First run follows the system (`AppsUseLightTheme`). An explicit choice is
written to `HKCU\Software\UnlockKit\Theme` and stops the following; right-click
on the switch to go back to *Follow Windows*. The choice lives in the registry
rather than a config file because the app already owns a key there.

## 9. Known trade-offs

| Trade-off | Why it is accepted |
| --- | --- |
| Reboot path requires an explicit flag | Surviving the session is a side effect the user must choose |
| TOCTOU window between verify and close | No conditional-close API exists; the alternative (close blind) can hit the wrong object |
| No ACL repair | Silently rewriting permissions on a user's files is a bigger change than the problem it solves; the diagnosis names the cause instead |
| No kernel driver | User-mode only means no signing, no BSOD risk, and no antivirus fight — at the cost of never beating a minifilter |
| Legacy registry verbs on Windows 11 | Sitting under *Show more options* is the price of not shipping an MSIX package |
