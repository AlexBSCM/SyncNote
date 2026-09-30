plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    // v2 lives next to v1 on test devices: own namespace/applicationId.
    namespace = "com.syncnote.v2"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.syncnote.v2"
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "0.1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    // No Room, no ORM: raw android.database.sqlite (see docs/schema.sql).
    implementation("androidx.camera:camera-core:1.3.4")
    implementation("androidx.camera:camera-camera2:1.3.4")
    implementation("androidx.camera:camera-lifecycle:1.3.4")
    implementation("androidx.camera:camera-view:1.3.4")
    implementation("com.google.mlkit:barcode-scanning:17.3.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.recyclerview:recyclerview:1.3.2")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.2")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.7.3")
    testImplementation("junit:junit:4.13.2")
    // Настоящий org.json для JVM-тестов: без него android.jar подсовывает
    // стабы ("not mocked") и любой JSONObject падает. На девайсе
    // используется фреймворковый org.json, в APK это не попадает.
    testImplementation("org.json:json:20240303")
}

// Единый источник схемы: docs/schema.sql копируется в assets ПЕРЕД сборкой.
// Коммитить сгенерированный assets/schema_v1.sql не нужно (см. .gitignore).
tasks.register<Copy>("syncSchemaAsset") {
    // src/android -> ../.. = корень репозитория, где лежит docs/.
    from(rootProject.file("../../docs/schema.sql"))
    into("src/main/assets")
    rename { "schema_v1.sql" }
}
tasks.named("preBuild") { dependsOn("syncSchemaAsset") }
