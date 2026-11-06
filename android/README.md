# TRELAY Android

Android 10+ (`minSdk 29`), package ID `com.trelay.android`, текущая версия `0.3.2-beta`.

Приложение использует Android `VpnService` только для выбранных пакетов. Для маршрутов телефонии рекомендуется TLINE (`com.tline.phone`), но поддерживаются и другие SIP-клиенты.

## Локальная сборка

В `local.properties` укажите путь к Android SDK. Для публичных сборок оставьте bootstrap-secret пустым.

```properties
sdk.dir=C\:\\Users\\<USER>\\AppData\\Local\\Android\\Sdk
```

Файл исключён из Git.

```powershell
.\gradlew.bat :app:assembleDebug
.\gradlew.bat :app:assembleRelease
```

Release-вариант собирается неподписанным. Подписывайте его сохранённым ключом приложения; смена сертификата потребует переустановки APK.
