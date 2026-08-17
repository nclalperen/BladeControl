<?xml version="1.0" encoding="utf-8"?>
<!--
  Removes BladeControl.Service.exe from the harvested runtime component group.

  The service executable is declared explicitly in Product.wxs because it is the key path of
  the component that carries ServiceInstall, ServiceControl and the DelayedAutostart value.
  Left in the harvest as well, the same file would be installed by two components, which
  breaks MSI component reference counting (ICE30). Everything else in the publish tree is
  harvested normally.
-->
<xsl:stylesheet version="1.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:wix="http://wixtoolset.org/schemas/v4/wxs"
                exclude-result-prefixes="wix">

  <xsl:output method="xml" indent="yes" />
  <xsl:strip-space elements="*" />

  <xsl:key name="ServiceExeComponents"
           match="wix:Component[wix:File[contains(@Source, 'BladeControl.Service.exe')]]"
           use="@Id" />

  <!-- Identity transform for everything not matched below. -->
  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <xsl:template match="wix:Component[key('ServiceExeComponents', @Id)]" />
  <xsl:template match="wix:ComponentRef[key('ServiceExeComponents', @Id)]" />

</xsl:stylesheet>
