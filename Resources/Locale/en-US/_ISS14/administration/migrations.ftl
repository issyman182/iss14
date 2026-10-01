# Admin alerts raised by the automatic migration path
migration-alert-auto = Auto-migrated returning player { $user } ({ $reason } match). Moved: { $detail }.
migration-alert-pending = Possible returning player { $user }: a same-username account exists but did not match by HWID/IP. Review it with the migrations tool.

# Manager results / errors
migration-error-same-user = Source and target are the same account.
migration-error-target-online = The target player must be offline to migrate data onto them.
migration-error-source-online = The source player must be offline to migrate their data.
migration-error-not-pending = That migration is no longer pending.
migration-rejected = Migration rejected.

# Command
cmd-migrations-server = This command can only be run by a player (it opens a window).

# Migrations oversight window
migrations-title = User Data Migrations
migrations-manual-header = Manual transfer
migrations-manual-explainer = Move all selected data from the source player onto the target. Both must be offline. The target keeps the surviving identity.
migrations-merge = Merge (keep both accounts' data)
migrations-merge-tooltip = On: combine both accounts — characters from both, play times summed, whitelists unioned. Off: replace the target's data with the source's.
migrations-source = From
migrations-target = To
migrations-player-placeholder = username or GUID
migrations-scope-gameplay = Gameplay
migrations-scope-whitelists = Whitelists
migrations-scope-bans = Bans & notes
migrations-scope-admin = Admin status
migrations-scope-admin-disabled = Only full admins can migrate admin status.
migrations-transfer = Transfer
migrations-error-resolve = Could not resolve "{ $input }" to a player (check the username/GUID).
migrations-error-ambiguous = "{ $name }" matches { $count } different accounts. Use the exact User ID instead (copy it from the player records window).
migrations-error-admin-perm = Only full admins can migrate admin status. Untick "Admin status" to proceed.

migrations-history-header = History
migrations-row-headline = { $source } → { $target }
migrations-row-meta = { $time } · { $kind } · { $status } · match: { $reason } · scope: { $scope }
migrations-row-detail = Moved: { $detail }
migrations-kind-auto = automatic
migrations-kind-manual = manual
migrations-approve = Approve
migrations-reject = Reject
