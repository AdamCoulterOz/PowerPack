data "azurerm_client_config" "current" {}

locals {
  resource_name_prefix         = trim(replace(lower(trimspace(var.name_prefix)), "/[^a-z0-9-]/", "-"), "-")
  storage_account_name_prefix  = substr(replace(local.resource_name_prefix, "/[^a-z0-9]/", ""), 0, 13)
  resource_group_name          = coalesce(var.resource_group_name, "rg-${local.resource_name_prefix}")
  service_plan_name            = local.resource_name_prefix
  application_insights_name    = local.resource_name_prefix
  function_app_name            = "${local.resource_name_prefix}-${random_string.function_app_suffix.result}"
  powerpack_api_display_name   = title(replace(local.resource_name_prefix, "-", " "))
  powerpack_api_app_role_name  = "PowerPack.Access"
  powerpack_api_identifier_uri = "api://${local.resource_name_prefix}"
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
  }

  app_role {
    allowed_member_types = ["Application"]
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
  name                = local.application_insights_name
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  application_type    = "web"
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

  auth_settings_v2 {
    auth_enabled           = true
    default_provider       = "azureactivedirectory"
    excluded_paths         = ["/api/packages/*/download"]
    require_authentication = true
    require_https          = true
    runtime_version        = "~1"
    unauthenticated_action = "Return401"

    login {
      token_store_enabled = false
    }

    active_directory_v2 {
      client_id            = azuread_application.api.client_id
      tenant_auth_endpoint = "https://login.microsoftonline.com/${data.azurerm_client_config.current.tenant_id}/v2.0"
      allowed_audiences = [
        local.powerpack_api_identifier_uri,
        azuread_application.api.client_id,
      ]
    }
  }

  app_settings = {
    "AzureWebJobsStorage__accountName"         = azurerm_storage_account.this.name
    "AzureWebJobsStorage__credential"          = "managedidentity"
    "PowerPack__Storage__AccountUrl"           = azurerm_storage_account.this.primary_table_endpoint
    "PowerPack__Storage__BlobAccountUrl"       = azurerm_storage_account.this.primary_blob_endpoint
    "PowerPack__Storage__PackageContainerName" = azurerm_storage_container.solution_packages.name
    "PowerPack__Downloads__TokenSigningKey"    = random_password.download_token_signing_key.result
    "PowerPack__Auth__ApplicationClientId"     = azuread_application.api.client_id
    "PowerPack__Auth__ApplicationIdUri"        = local.powerpack_api_identifier_uri
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
