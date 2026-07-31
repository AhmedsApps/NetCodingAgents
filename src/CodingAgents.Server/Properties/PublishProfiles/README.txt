Publish profiles were removed from this copy because they contained live hosting
credentials (deployment user name, deployment password and the private site URL).

TO ADD YOUR OWN
---------------
Do NOT hand-write these files. Generate them from your hosting provider instead:

1. Download the ".PublishSettings" file from your host's control panel
   (Azure, SmarterASP, Plesk, etc. all offer a "publish profile" download).
2. In Visual Studio: right-click the project > Publish > Import Profile,
   and select the downloaded file.
3. Visual Studio recreates this folder with:
      <name>.PublishSettings   - contains userName / userPWD  (SECRET)
      <name>.pubxml            - destination URL and publish options
      <name>.pubxml.user       - local-only state, encrypted password

KEEP THEM OUT OF SOURCE CONTROL
-------------------------------
These files contain deployment secrets. Add the following to your .gitignore:

    **/Properties/PublishProfiles/
    *.pubxml
    *.pubxml.user
    *.PublishSettings

If you ever committed a real publish profile, rotate the deployment password in
your hosting control panel - the old one must be considered compromised.
