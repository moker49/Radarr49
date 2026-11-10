# Deploying selective import retries

1. Before deploying, check Settings > Profiles > SDR/HDR > Language. Both currently
   use **Original**. Your database interprets this as Korean for Snowpiercer and
   French for Two Prosecutors. Keep that if intended; choose English or Any if those
   match what you actually want. Otherwise repeated English-only downloads can
   continue to be rejected correctly under the selected policy.
2. Rebuild with `scripts/build-container-to-server.bat`, then run the exported
   `run-container.bat` on the destination host. Back up the container configuration
   before the database upgrade; migration 254 adds version-specific blocklisting.
3. Check Settings > Download Clients > Failed Download Handling: enable automatic
   redownloading. Existing completed jobs remaining in SABnzbd will be rechecked.
   Fall HDR and Predator SDR should be blocklisted/replaced when their folders
   contain only the unsupported files shown. Snowpiercer and Two Prosecutors will
   import or be replaced according to the selected language setting. Failed-job
   removal follows your existing download-client settings and may delete rejected
   download contents. Existing successfully imported movie files are preserved.
4. The Resident's unfinished operation 81 should resume through startup recovery.
   Let it verify the existing publication. If it remains blocked, inspect the
   operation error using the read-only helper below; failures now persist their
   underlying error instead of being hidden by an assignment warning.

Do not delete `.radarr-version-imports` folders to clear a blocked job. They may
contain recoverable source/backup files. Completed operation directories can be
left in place.

## The Invite's abandoned HDR download

This is separate from release-content failures: old operation 20 was interrupted
while you force-reset the download. It has no previous or registered new movie
file. The new API allows cancellation at that checkpoint only if the destination
has not been published.

Inspect the current operations from a machine that can reach Radarr:

```powershell
.\scripts\reconcile-current-imports.ps1 -BaseUrl 'http://SERVER:7878'
```

The helper prompts for the API key without echoing it. This default invocation is
read-only.

To clear the abandoned operation, first remove the original
`The.Invite.2026.2160p.HMAX.WEB-DL.DDP.5.1.DV.HDR.H.265-PiRaTeS` job from SABnzbd's
queue/history if it is still there. Then run:

```powershell
.\scripts\reconcile-current-imports.ps1 -BaseUrl 'http://SERVER:7878' -ClearAbandonedInvite
```

The helper verifies the movie, operation, reservation, and checkpoint, cancels the
unpublished operation, asks Radarr to verify the client job is gone before freeing
its reservation, then searches only the HDR version. It does not delete movie or
staging files. If the client job is still found, remove it in SABnzbd and rerun the
helper. Replace SERVER and the port with your actual Radarr address.
