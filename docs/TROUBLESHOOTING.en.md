[简体中文](TROUBLESHOOTING.md) · **English**

# Troubleshooting

Organised by the error you are seeing. If your case is not here, run

```console
unlockkit-cli "path\to\thing" --json -v
```

and include the output in the issue. Add `UNLOCKKIT_DEBUG=1` to the environment
for the scanner's internal counters.

---

## "The action can't be completed because the folder or a file in it is open in another program"

This is `ERROR_SHARING_VIOLATION` (`0x20`, or `0x80070020` from `robocopy`,
`.NET`, PowerShell).

Something has the object open and did not pass `FILE_SHARE_DELETE`. UnlockKit
reports this as `LOCKED`, names the process, and closing the handles is usually
enough.

**If the folder is the target**, the holder is almost never the folder itself.
It is a file or subdirectory inside it, which is why UnlockKit reports
`LOCKED (subtree)` and lists the specific paths. Deleting the folder means
deleting those children, so their locks are your problem even though the folder
object looks free.

Two outcomes are common after `Unlock`:

- **The handles come back.** A watcher re-opened them within milliseconds. This
  is expected for `TSVNCache.exe`, `SearchIndexer.exe`, `OneDrive.exe`,
  `Dropbox.exe`, and antivirus. Use `Delete` rather than `Unlock` — the tree
  delete closes the whole subtree once and then works faster than the watcher can
  relock. If that still fails, fix the watcher (see below).
- **Nothing was closeable.** Every handle was refused or unreadable. Refused
  means a deny-listed or PPL-protected process; unreadable means you need to run
  elevated. The report distinguishes the two.

---

## "Access is denied" / "You need permission to perform this action"

`ERROR_ACCESS_DENIED` (`0x5`, `0x80070005`).

UnlockKit reports this as `ACL BLOCKS DELETE` when no handle holds the file. That
verdict means exactly what it says: **this is not a lock**, and no unlocker can
help. The account lacks the `DELETE` right on the object or on one of its parent
directories.

Check first that it is not simply a read-only or system attribute, then look at
the ACL:

```console
icacls "D:\path\to\thing"
```

Fixing this means taking ownership and granting yourself `DELETE`, which changes
the security of the file. UnlockKit deliberately does not do that silently —
taking ownership of a file you did not intend to touch is a worse outcome than a
failed delete.

---

## It says `KERNEL-HELD LOCK` — a filter driver holds it

`ERROR_SHARING_VIOLATION` with **zero** user-mode handles pointing at the target.

This means the lock lives in a kernel filter driver (minifilter): antivirus,
backup agent, endpoint security, cloud sync, or a filesystem filter. User-mode
code cannot close those handles — not this tool, not any other. Any tool claiming
otherwise is either installing a driver of its own, or lying.

What actually works:

1. **Pause the culprit.** Antivirus real-time protection, OneDrive/Dropbox sync,
   backup jobs.
2. `unlockkit-cli delete "path" --reboot` — registers the path in
   `PendingFileRenameOperations`, and `SMSS` deletes it before any user-mode code
   runs at the next boot.
3. Reboot, delete manually, then find the exclusions list.

Identifying the driver: in the dialog that names no process, check
`fltmc filters` for installed minifilters, and the driver list of your security
product.

---

## `STATUS_CANNOT_DELETE` — the file is a running program

You are trying to delete an `.exe` or `.dll` that is currently mapped as an image
section. Windows refuses, and it is right to: deleting the backing file of a
running image is undefined behaviour. `Cannot delete: file is mapped as an image
section (running exe/dll)`.

Stop the process first. If it is a service, stop the service. If it is loaded into
`explorer.exe` (a shell extension), the file stays mapped until Explorer restarts.

---

## "Unlock" reports refusals — `PPL-protected (level 0x..)`

Protected Process Light. `MsMpEng.exe` (Defender), `services.exe`, `lsass.exe`,
`csrss.exe` and friends cannot be opened even with `SeDebugPrivilege`. UnlockKit
prints the protection level so the refusal is explainable rather than mysterious.

This is not a bug to be fixed. Closing handles in `csrss` or `winlogon` can stop
the desktop from responding; the deny-list exists for that reason.

---

## It was fine before, now it comes back every time — watcher processes

A process that watches a directory (version-control icon overlays, search
indexers, sync clients) re-opens handles continuously. Closing them is treating
the symptom. The durable fix is to stop the watcher from watching that path.

| Process | Durable fix |
| --- | --- |
| `TSVNCache.exe` (TortoiseSVN) | Settings → Icon Overlays → **Exclude paths** → add the folder. Status cache is the other half — set it to *none* or *shell* |
| `TGitCache.exe` (TortoiseGit) | Same dialog, same exclusion list |
| `SearchIndexer.exe` | Indexing Options → Modify → uncheck the folder |
| `OneDrive.exe` | Settings → Account → Choose folders, or pause syncing |
| `Dropbox.exe` / `GoogleDriveFS.exe` | Selective sync — remove the folder from sync |
| Antivirus | Add the folder to real-time scan exclusions |
| `explorer.exe` | Usually a thumbnail or preview handler: switch the folder view to *Details*, or clear the thumbnail cache |

`unlockkit-cli close "path"` on such a folder will report handles closed and then
the same process holding them again a second later. That is the signature.

---

## `NOT LOCKED` but the delete still fails

The scanner found no handle and `DELETE` was granted, yet the delete failed. The
usual causes:

- **Path length.** Over `MAX_PATH` (260) without the `\\?\` prefix. UnlockKit
  uses the extended-length form internally, but the caller may not.
- **A parent directory is the problem**, not the file: read-only, ACL, or a
  reparse point. Delete fails on the parent while reporting the child.
- **The target moved** between scan and delete. Re-run.
- **A filter driver** that fails the delete instead of blocking the open — this
  shows up as an unexpected `NTSTATUS` and is worth reporting with `--json -v`.

---

## The right-click entry appears under "Show more options"

Expected on Windows 11. UnlockKit writes a classic registry verb; the top-level
Windows 11 menu only accepts MSIX-packaged `IExplorerCommand` handlers with app
identity. Keyboard workaround: select the file and press `Shift+F10`.

---

## The menu entry is there but does nothing

The verb launches
`%LOCALAPPDATA%\Programs\UnlockKit\UnlockKit.exe`. Check that the file exists —
if the app was moved or deleted without removing the menu first, the entry
becomes a dead link. Re-run the app and click **Install context menu** twice
(remove, then install) to rewrite the paths.

---

## Reporting a bug

```console
unlockkit-cli "path" --json -v > report.json
```

Include:

- Windows version (`winver`),
- whether the window was elevated,
- the verdict line,
- what you expected instead.

Never paste a report that contains paths you consider private — the JSON includes
resolved paths for subtree matches.
