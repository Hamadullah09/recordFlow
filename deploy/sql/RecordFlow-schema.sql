IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AdminColumns] (
        [Id] int NOT NULL IDENTITY,
        [Slot] int NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [DisplayLabel] nvarchar(100) NOT NULL,
        [FieldType] nvarchar(20) NOT NULL,
        [IsActive] bit NOT NULL,
        [IsVisible] bit NOT NULL,
        [IsRequired] bit NOT NULL,
        [AllowUserEdit] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [SourceField] nvarchar(200) NULL,
        [DefaultValue] nvarchar(500) NULL,
        [Options] nvarchar(2000) NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(256) NULL,
        CONSTRAINT [PK_AdminColumns] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AdminColumns_Slot] CHECK ([Slot] BETWEEN 1 AND 6)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AppSettings] (
        [Key] nvarchar(100) NOT NULL,
        [Value] nvarchar(2000) NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(256) NULL,
        CONSTRAINT [PK_AppSettings] PRIMARY KEY ([Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetRoles] (
        [Id] nvarchar(450) NOT NULL,
        [Name] nvarchar(256) NULL,
        [NormalizedName] nvarchar(256) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoles] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] bigint NOT NULL IDENTITY,
        [TimestampUtc] datetime2 NOT NULL,
        [Category] nvarchar(30) NOT NULL,
        [Action] nvarchar(100) NOT NULL,
        [Succeeded] bit NOT NULL,
        [UserId] nvarchar(450) NULL,
        [UserName] nvarchar(256) NULL,
        [EntityType] nvarchar(100) NULL,
        [EntityId] nvarchar(100) NULL,
        [Details] nvarchar(2000) NULL,
        [IpAddress] nvarchar(64) NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [Companies] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(200) NOT NULL,
        [NormalizedName] nvarchar(200) NOT NULL,
        [Phone] nvarchar(30) NULL,
        [AddressLine1] nvarchar(200) NULL,
        [AddressLine2] nvarchar(200) NULL,
        [City] nvarchar(100) NULL,
        [State] nvarchar(2) NULL,
        [ZipCode] nvarchar(10) NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_Companies] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [DataProtectionKeys] (
        [Id] int NOT NULL IDENTITY,
        [FriendlyName] nvarchar(max) NULL,
        [Xml] nvarchar(max) NULL,
        CONSTRAINT [PK_DataProtectionKeys] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [FormFields] (
        [Id] int NOT NULL IDENTITY,
        [Key] nvarchar(100) NOT NULL,
        [Label] nvarchar(150) NOT NULL,
        [Section] nvarchar(20) NOT NULL,
        [FieldType] nvarchar(20) NOT NULL,
        [IsRequired] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [RecipientEditable] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [CsvAliases] nvarchar(1000) NULL,
        [HelpText] nvarchar(300) NULL,
        [MaxLength] int NOT NULL,
        [Options] nvarchar(2000) NULL,
        CONSTRAINT [PK_FormFields] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetRoleClaims] (
        [Id] int NOT NULL IDENTITY,
        [RoleId] nvarchar(450) NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUsers] (
        [Id] nvarchar(450) NOT NULL,
        [FullName] nvarchar(100) NOT NULL,
        [CompanyId] int NULL,
        [IsDisabled] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [LastLoginAtUtc] datetime2 NULL,
        [UserName] nvarchar(256) NULL,
        [NormalizedUserName] nvarchar(256) NULL,
        [Email] nvarchar(256) NULL,
        [NormalizedEmail] nvarchar(256) NULL,
        [EmailConfirmed] bit NOT NULL,
        [PasswordHash] nvarchar(max) NULL,
        [SecurityStamp] nvarchar(max) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        [PhoneNumber] nvarchar(max) NULL,
        [PhoneNumberConfirmed] bit NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [LockoutEnabled] bit NOT NULL,
        [AccessFailedCount] int NOT NULL,
        CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUsers_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE SET NULL
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserClaims] (
        [Id] int NOT NULL IDENTITY,
        [UserId] nvarchar(450) NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserLogins] (
        [LoginProvider] nvarchar(450) NOT NULL,
        [ProviderKey] nvarchar(450) NOT NULL,
        [ProviderDisplayName] nvarchar(max) NULL,
        [UserId] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
        CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserRoles] (
        [UserId] nvarchar(450) NOT NULL,
        [RoleId] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserTokens] (
        [UserId] nvarchar(450) NOT NULL,
        [LoginProvider] nvarchar(450) NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [Value] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
        CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [Orders] (
        [Id] int NOT NULL IDENTITY,
        [PublicId] uniqueidentifier NOT NULL,
        [OrderNumber] nvarchar(32) NOT NULL,
        [UserId] nvarchar(450) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [ServiceName] nvarchar(200) NOT NULL,
        [ServiceDescription] nvarchar(500) NULL,
        [Subtotal] decimal(18,2) NOT NULL,
        [Tax] decimal(18,2) NOT NULL,
        [Fee] decimal(18,2) NOT NULL,
        [Total] decimal(18,2) NOT NULL,
        [Currency] nvarchar(3) NOT NULL,
        [BillingName] nvarchar(100) NOT NULL,
        [BillingEmail] nvarchar(254) NOT NULL,
        [BillingAddressLine1] nvarchar(200) NOT NULL,
        [BillingAddressLine2] nvarchar(200) NULL,
        [BillingCity] nvarchar(100) NOT NULL,
        [BillingState] nvarchar(2) NOT NULL,
        [BillingZip] nvarchar(10) NOT NULL,
        [PaymentMethod] nvarchar(20) NOT NULL,
        [ContactId] nvarchar(100) NOT NULL,
        [StoreName] nvarchar(250) NULL,
        [Provider] nvarchar(30) NOT NULL,
        [ProviderSessionId] nvarchar(255) NULL,
        [ProviderPaymentId] nvarchar(255) NULL,
        [CardBrand] nvarchar(30) NULL,
        [CardLast4] nvarchar(4) NULL,
        [FailureReason] nvarchar(500) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [PaidAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Orders_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE TABLE [FinalizedRecords] (
        [Id] int NOT NULL IDENTITY,
        [PublicId] uniqueidentifier NOT NULL,
        [ConfirmationNumber] nvarchar(32) NOT NULL,
        [OrderId] int NOT NULL,
        [UserId] nvarchar(450) NOT NULL,
        [CompanyName] nvarchar(200) NULL,
        [ContactId] nvarchar(100) NOT NULL,
        [StoreName] nvarchar(250) NULL,
        [Response] nvarchar(1) NULL,
        [FieldsJson] nvarchar(max) NOT NULL,
        [AdminColumnsJson] nvarchar(max) NOT NULL,
        [RecordCreatedAtUtc] datetime2 NOT NULL,
        [RecipientSubmittedAtUtc] datetime2 NULL,
        [ConfirmedAtUtc] datetime2 NOT NULL,
        [ConfirmedByUserId] nvarchar(450) NOT NULL,
        [ConfirmedByName] nvarchar(256) NOT NULL,
        [ClosedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_FinalizedRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FinalizedRecords_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AdminColumns_Slot] ON [AdminColumns] ([Slot]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [AspNetRoleClaims] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [RoleNameIndex] ON [AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUserClaims_UserId] ON [AspNetUserClaims] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUserLogins_UserId] ON [AspNetUserLogins] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [AspNetUserRoles] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [EmailIndex] ON [AspNetUsers] ([NormalizedEmail]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUsers_CompanyId] ON [AspNetUsers] ([CompanyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UserNameIndex] ON [AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_Category_TimestampUtc] ON [AuditLogs] ([Category], [TimestampUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_TimestampUtc] ON [AuditLogs] ([TimestampUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Companies_NormalizedName] ON [Companies] ([NormalizedName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FinalizedRecords_ConfirmationNumber] ON [FinalizedRecords] ([ConfirmationNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_FinalizedRecords_ContactId] ON [FinalizedRecords] ([ContactId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FinalizedRecords_OrderId] ON [FinalizedRecords] ([OrderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FinalizedRecords_PublicId] ON [FinalizedRecords] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_FinalizedRecords_UserId_ConfirmedAtUtc] ON [FinalizedRecords] ([UserId], [ConfirmedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FormFields_Key] ON [FormFields] ([Key]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Orders_OrderNumber] ON [Orders] ([OrderNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Orders_ProviderSessionId] ON [Orders] ([ProviderSessionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Orders_PublicId] ON [Orders] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Orders_Status] ON [Orders] ([Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Orders_UserId_CreatedAtUtc] ON [Orders] ([UserId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005111635_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261005111635_InitialCreate', N'10.0.12');
END;

COMMIT;
GO

