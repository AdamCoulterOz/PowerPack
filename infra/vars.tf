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

variable "maximum_instance_count" {
  type    = number
  default = 1
}

variable "instance_memory_in_mb" {
  type    = number
  default = 2048
}
