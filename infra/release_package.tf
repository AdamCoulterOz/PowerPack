locals {
  # Release packaging rewrites this value in the published module artifact so the
  # released module carries an immutable pointer to its matching API package.
  baked_api_package_uri = null
}
