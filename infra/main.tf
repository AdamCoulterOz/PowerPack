data "azurerm_client_config" "current" {}

locals {
  resource_name_prefix         = trim(replace(lower(trimspace(var.name_prefix)), "/[^a-z0-9-]/", "-"), "-")
  storage_account_name_prefix  = substr(replace(local.resource_name_prefix, "/[^a-z0-9]/", ""), 0, 13)
  resource_group_name          = coalesce(var.resource_group_name, "rg-${local.resource_name_prefix}")
  service_plan_name            = local.resource_name_prefix
  application_insights_name    = local.resource_name_prefix
  key_vault_name               = substr("kv-${local.storage_account_name_prefix}-${substr(random_string.storage_account_suffix.result, 0, 8)}", 0, 24)
  function_app_name            = "${local.resource_name_prefix}-${random_string.function_app_suffix.result}"
  powerpack_api_display_name   = title(replace(local.resource_name_prefix, "-", " "))
  powerpack_api_app_role_name  = "PowerPack.Access"
  powerpack_api_identifier_uri = "api://${local.resource_name_prefix}"
  resolved_api_package_uri     = local.baked_api_package_uri
}

resource "random_string" "storage_account_suffix" {
  length  = 10
  upper   = false
  special = false

  lifecycle {
    ignore_changes = all
  }
}

resource "random_string" "function_app_suffix" {
  length  = 10
  upper   = false
  special = false

  lifecycle {
    ignore_changes = all
  }
}

resource "random_uuid" "powerpack_api_app_role_id" {}

resource "random_uuid" "powerpack_api_scope_id" {}

resource "random_password" "download_token_signing_key" {
  length  = 64
  special = false
}

resource "azuread_application" "api" {
  display_name     = local.powerpack_api_display_name
  sign_in_audience = "AzureADMyOrg"
  identifier_uris  = [local.powerpack_api_identifier_uri]
  owners           = [data.azurerm_client_config.current.object_id]

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      admin_consent_description  = "Call the PowerPack API as the signed-in user."
      admin_consent_display_name = "Access PowerPack API"
      enabled                    = true
      id                         = random_uuid.powerpack_api_scope_id.result
      type                       = "User"
      user_consent_description   = "Call the PowerPack API as you."
      user_consent_display_name  = "Access PowerPack API"
      value                      = local.powerpack_api_app_role_name
    }
  }

  app_role {
    allowed_member_types = ["Application", "User"]
    description          = "Call the PowerPack API."
    display_name         = "Access PowerPack API"
    enabled              = true
    id                   = random_uuid.powerpack_api_app_role_id.result
    value                = local.powerpack_api_app_role_name
  }

  lifecycle {
    ignore_changes = [owners]
  }
}

resource "azuread_service_principal" "api" {
  client_id                    = azuread_application.api.client_id
  app_role_assignment_required = true
  owners                       = [data.azurerm_client_config.current.object_id]

  lifecycle {
    ignore_changes = [owners]
  }
}

resource "azurerm_resource_group" "this" {
  name     = local.resource_group_name
  location = var.location
}

resource "azurerm_storage_account" "this" {
  name                     = "${local.storage_account_name_prefix}${random_string.storage_account_suffix.result}"
  resource_group_name      = azurerm_resource_group.this.name
  location                 = azurerm_resource_group.this.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"
}

resource "azurerm_storage_container" "function_package" {
  name                  = "function-releases"
  storage_account_id    = azurerm_storage_account.this.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "solution_packages" {
  name                  = "solution-packages"
  storage_account_id    = azurerm_storage_account.this.id
  container_access_type = "private"
}

resource "azurerm_service_plan" "this" {
  name                = local.service_plan_name
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  os_type             = "Linux"
  sku_name            = "FC1"
}

resource "azurerm_application_insights" "this" {
  name                          = local.application_insights_name
  location                      = azurerm_resource_group.this.location
  resource_group_name           = azurerm_resource_group.this.name
  application_type              = "web"
  local_authentication_disabled = true
}

resource "azurerm_key_vault" "this" {
  name                       = local.key_vault_name
  location                   = azurerm_resource_group.this.location
  resource_group_name        = azurerm_resource_group.this.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  soft_delete_retention_days = 7
  purge_protection_enabled   = false

  access_policy {
    tenant_id = data.azurerm_client_config.current.tenant_id
    object_id = data.azurerm_client_config.current.object_id

    secret_permissions = [
      "Delete",
      "Get",
      "List",
      "Purge",
      "Recover",
      "Set",
    ]
  }
}

resource "azurerm_key_vault_secret" "download_token_signing_key" {
  name         = "download-token-signing-key"
  key_vault_id = azurerm_key_vault.this.id
  value        = random_password.download_token_signing_key.result
}

resource "azurerm_function_app_flex_consumption" "this" {
  name                = local.function_app_name
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  service_plan_id     = azurerm_service_plan.this.id

  # Keep always-ready unset so the Flex Consumption app remains eligible to scale to zero.
  storage_container_type      = "blobContainer"
  storage_container_endpoint  = "${azurerm_storage_account.this.primary_blob_endpoint}${azurerm_storage_container.function_package.name}"
  storage_authentication_type = "SystemAssignedIdentity"
  runtime_name                = "dotnet-isolated"
  runtime_version             = "10.0"
  maximum_instance_count      = var.maximum_instance_count
  instance_memory_in_mb       = var.instance_memory_in_mb

  https_only = true

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_insights_connection_string = azurerm_application_insights.this.connection_string
  }

  app_settings = {
    "APPLICATIONINSIGHTS_AUTHENTICATION_STRING" = "Authorization=AAD"
    "AzureWebJobsStorage__accountName"          = azurerm_storage_account.this.name
    "AzureWebJobsStorage__credential"           = "managedidentity"
    "AzureWebJobsStorage"                       = ""
    "PowerPack__Storage__AccountUrl"            = azurerm_storage_account.this.primary_table_endpoint
    "PowerPack__Storage__BlobAccountUrl"        = azurerm_storage_account.this.primary_blob_endpoint
    "PowerPack__Storage__PackageContainerName"  = azurerm_storage_container.solution_packages.name
    "PowerPack__Downloads__TokenSigningKey"     = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.download_token_signing_key.versionless_id})"
    "PowerPack__Auth__ApplicationClientId"      = azuread_application.api.client_id
    "PowerPack__Auth__ApplicationIdUri"         = local.powerpack_api_identifier_uri
    "PowerPack__Auth__TenantId"                 = data.azurerm_client_config.current.tenant_id
    "PowerPack__Auth__RequiredRole"             = local.powerpack_api_app_role_name
    "PowerPack__Auth__RequiredScope"            = local.powerpack_api_app_role_name
  }
}

resource "azapi_resource" "function_app_onedeploy" {
  type                      = "Microsoft.Resources/deployments@2021-04-01"
  name                      = "function-app-onedeploy"
  parent_id                 = azurerm_resource_group.this.id
  schema_validation_enabled = false

  body = {
    properties = {
      mode = "Incremental"
      template = {
        "$schema"      = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#"
        contentVersion = "1.0.0.0"
        parameters = {
          functionAppName = {
            type = "string"
          }
          location = {
            type = "string"
          }
          packageUri = {
            type = "string"
          }
        }
        resources = [
          {
            type       = "Microsoft.Web/sites/extensions"
            apiVersion = "2022-09-01"
            name       = "[format('{0}/{1}', parameters('functionAppName'), 'onedeploy')]"
            location   = "[parameters('location')]"
            properties = {
              packageUri  = "[parameters('packageUri')]"
              remoteBuild = false
            }
          }
        ]
      }
      parameters = {
        functionAppName = {
          value = azurerm_function_app_flex_consumption.this.name
        }
        location = {
          value = azurerm_resource_group.this.location
        }
        packageUri = {
          value = local.resolved_api_package_uri
        }
      }
    }
  }

  depends_on = [
    azurerm_function_app_flex_consumption.this,
    azurerm_role_assignment.function_host_blob_owner,
    azurerm_role_assignment.function_host_table_contributor,
    azurerm_role_assignment.function_host_monitoring_metrics_publisher,
    azurerm_key_vault_access_policy.function_host,
  ]

  lifecycle {
    precondition {
      condition     = try(length(trimspace(local.resolved_api_package_uri)) > 0, false)
      error_message = "No baked API package URI is configured. Use the released module artifact that was packaged with its matching API release asset."
    }
  }
}

resource "azurerm_role_assignment" "function_host_blob_owner" {
  scope                = azurerm_storage_account.this.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_function_app_flex_consumption.this.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "function_host_table_contributor" {
  scope                = azurerm_storage_account.this.id
  role_definition_name = "Storage Table Data Contributor"
  principal_id         = azurerm_function_app_flex_consumption.this.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "function_host_monitoring_metrics_publisher" {
  scope                = azurerm_application_insights.this.id
  role_definition_name = "Monitoring Metrics Publisher"
  principal_id         = azurerm_function_app_flex_consumption.this.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_key_vault_access_policy" "function_host" {
  key_vault_id = azurerm_key_vault.this.id
  tenant_id    = azurerm_function_app_flex_consumption.this.identity[0].tenant_id
  object_id    = azurerm_function_app_flex_consumption.this.identity[0].principal_id

  secret_permissions = [
    "Get",
    "List",
  ]
}
