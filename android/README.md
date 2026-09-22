# TRELAY Android

Android 10+ (`minSdk 29`), package ID `com.trelay.android`, текущая версия `0.3-beta`.

Приложение использует Android `VpnService` только для выбранных пакетов. Для маршрутов телефонии рекомендуется TLINE (`com.tline.phone`), но поддерживаются и другие SIP-клиенты.

## Локальная сборка

В `local.properties` укажите Android SDK и локальный bootstrap-secret подписок:

```properties
sdk.dir=C\:\\Users\\<USER>\\AppData\\Local\\Android\\Sdk
trelay.bootstrapSecret=...
```

Файл исключён из Git.

```powershell
.\gradlew.bat :app:assembleDebug
.\gradlew.bat :app:assembleRelease
```

Release-вариант собирается неподписанным. Подписывайте его сохранённым ключом приложения; смена сертификата потребует переустановки APK.
