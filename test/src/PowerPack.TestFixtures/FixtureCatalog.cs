namespace PowerPack.TestFixtures;

public static class FixtureCatalog
{
    public static IReadOnlyList<SolutionPackageFixture> All { get; } =
    [
        new(
            "SharedFoundation",
            "1.0.0.0",
            "ExamplePublisher",
            [],
            [
                new(
                    "pp_shared_sql",
                    "/providers/Microsoft.PowerApps/apis/shared_sql",
                    "Shared SQL",
                    """
                    Shared SQL connection used across the fixture set.
                    [requirements]
                    auth: service
                    roles:
                    - integration
                    permissions:
                    - sql/application/execute
                    """
                ),
            ]
        ),
        new(
            "TableToolkit",
            "1.1.0.0",
            "ExamplePublisher",
            [
                new("SharedFoundation", "1.0.0.0"),
            ],
            [
                new(
                    "pp_table_sharepoint",
                    "/providers/Microsoft.PowerApps/apis/shared_sharepointonline",
                    "Table Toolkit SharePoint",
                    """
                    SharePoint connection for table-driven assets.
                    [requirements]
                    auth: user
                    roles:
                    - maker
                    permissions:
                    - sharepoint/delegated/AllSites.Read
                    """
                ),
            ]
        ),
        new(
            "ExperienceHub",
            "2.0.0.0",
            "ExamplePublisher",
            [
                new("SharedFoundation", "1.0.0.0"),
                new("TableToolkit", "1.1.0.0"),
            ],
            [
                new(
                    "pp_experience_approvals",
                    "/providers/Microsoft.PowerApps/apis/shared_approvals",
                    "Experience Approvals",
                    """
                    Approval workflow connection for experience orchestration.
                    [requirements]
                    auth: user
                    roles:
                    - approver
                    permissions:
                    - approvals/delegated/Approval.ReadWrite.All
                    """
                ),
            ]
        ),
        new(
            "WorkspaceForms",
            "1.44.0.0",
            "ExamplePublisher",
            [
                new("ExperienceHub", "2.0.0.0"),
                new("TableToolkit", "1.1.0.0"),
            ],
            [
                new(
                    "pp_workspace_notify",
                    "/providers/Microsoft.PowerApps/apis/shared_teams",
                    "Workspace Notifications",
                    """
                    Notifications for workspace form updates.
                    [requirements]
                    auth: service
                    roles:
                    - notifier
                    permissions:
                    - teams/application/ChannelMessage.Send
                    """
                ),
            ]
        ),
    ];
}
