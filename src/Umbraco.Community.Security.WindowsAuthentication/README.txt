
== Windows Authentication - client script ==

The backoffice client script is a single TypeScript module, built with Vite into
`wwwroot\App_Plugins\WindowsAuthentication`. The built output is committed: the package
projects (.v17 and .v18) mirror that folder into their own wwwroot at build time and do not
run npm themselves.

== Requirements ==
* Node LTS Version 20.19.0+
* Use a tool such as NVM (Node Version Manager) for your OS to help manage multiple versions of Node

== Node Version Manager tools ==
* https://github.com/coreybutler/nvm-windows
* https://github.com/nvm-sh/nvm
* https://docs.volta.sh/guide/getting-started

== Steps ==
* Open a terminal inside the `\Client` folder
* Run `npm install` to install all the dependencies
* Run `npm run build` to type-check and build the script
* The build output is written to `wwwroot\App_Plugins\WindowsAuthentication\windows-authentication-[hash].js`,
  and `scripts/update-manifest.js` points `umbraco-package.json` at the hashed file name
* Rebuild the solution (or a test site) so the package projects pick up the new files

== File Watching ==
* From the `\Client` folder run `npm run watch` to rebuild the script when the *.ts files change
* Watch mode does not update the manifest. Run `npm run build` once you are done, so the
  manifest points at the final hashed file name

== Other Resources ==
* Umbraco Docs - https://docs.umbraco.com/umbraco-cms/customizing/overview
* App entry points - https://docs.umbraco.com/umbraco-cms/customizing/extending-overview/extension-types/app-entry-point
