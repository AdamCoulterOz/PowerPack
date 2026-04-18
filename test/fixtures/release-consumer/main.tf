terraform {
  required_version = ">= 1.9.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  features {}
}

module "powerpack" {
  source = "https://github.com/AdamCoulterOz/PowerPack/releases/download/v0.1.0/module-0.1.0.zip"

  name_prefix = "powerpack-release-test"
  location    = "australiaeast"
}
