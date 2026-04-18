output "resource_group_name" {
  value = azurerm_resource_group.this.name
}

output "storage_account_name" {
  value = azurerm_storage_account.this.name
}

output "storage_container_name" {
  value = azurerm_storage_container.function_package.name
}

output "solution_package_container_name" {
  value = azurerm_storage_container.solution_packages.name
}

output "function_app_name" {
  value = azurerm_function_app_flex_consumption.this.name
}

output "function_app_default_hostname" {
  value = azurerm_function_app_flex_consumption.this.default_hostname
}

output "base_url" {
  value = "https://${azurerm_function_app_flex_consumption.this.default_hostname}"
}

output "api_package_uri" {
  value = local.resolved_api_package_uri
}

output "application_client_id" {
  value = azuread_application.api.client_id
}

output "application_identifier_uri" {
  value = local.powerpack_api_identifier_uri
}

output "service_principal_object_id" {
  value = azuread_service_principal.api.object_id
}

output "app_role_id" {
  value = random_uuid.powerpack_api_app_role_id.result
}

output "application_insights_connection_string" {
  value     = azurerm_application_insights.this.connection_string
  sensitive = true
}
