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
    testImplementation("junit:junit:4.13.2")
}
