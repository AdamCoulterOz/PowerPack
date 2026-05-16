variable "name_prefix" {
  type    = string
  default = "powerpack"

  validation {
    condition     = length(trimspace(var.name_prefix)) > 0
    error_message = "name_prefix must not be empty."
  }
}

variable "location" {
  type    = string
  default = "australiaeast"
}

variable "resource_group_name" {
  type    = string
  default = null
}

variable "storage_account_name" {
  type    = string
  default = null
}

variable "service_plan_name" {
  type    = string
  default = null
}

variable "application_insights_name" {
  type    = string
  default = null
}

variable "function_app_name" {
  type    = string
  default = null
}

variable "powerpack_api_display_name" {
  type    = string
  default = null
}

variable "powerpack_api_identifier_uri" {
  type    = string
  default = null
}

variable "maximum_instance_count" {
  type    = number
  default = 1
}

variable "instance_memory_in_mb" {
  type    = number
  default = 2048
}
